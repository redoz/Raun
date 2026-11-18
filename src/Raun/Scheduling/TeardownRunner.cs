using System.Diagnostics;
using Raun.Model;

namespace Raun.Scheduling;

/// <summary>
/// Runs a scenario's registered cleanups and reports them as the teardown node's one result.
/// </summary>
/// <remarks>
/// It runs after the scheduling loop, so a cancelled scenario token has already stopped the graph
/// and cannot suppress the cleanups that exist to prevent leaks. Cleanup delegates are invoked
/// directly, never through a step's cancellation source; each gets a deadline of its own, watched by
/// a watchdog of its own.
/// </remarks>
internal sealed class TeardownRunner(TimeProvider timeProvider, TimeSpan cancellationGracePeriod, TimeSpan cleanupTimeout)
{
    /// <summary>What one teardown needs from the scenario that is ending.</summary>
    /// <param name="Definition">The scenario.</param>
    /// <param name="Node">Its teardown node.</param>
    /// <param name="Title">The node's formatted title.</param>
    /// <param name="Cleanups">The registered cleanups, in the order to run them.</param>
    /// <param name="ScenarioSucceeded">Whether nothing failed, timed out or was cancelled; decides
    /// optional cleanups under <see cref="Run.OnSuccess"/>.</param>
    /// <param name="StartedAt">When teardown starts, on the scenario's timeline.</param>
    /// <param name="SimulatedTime">True on a simulated timeline, where teardown takes no time.</param>
    /// <param name="Services">The scenario's service provider.</param>
    /// <param name="Formatting">Title and note formatting services.</param>
    /// <param name="Inputs">The scenario's step outputs.</param>
    /// <param name="ScenarioStart">The scenario's start, for log offsets.</param>
    /// <param name="BeforeTermination">The run's last word if a cleanup has to be killed with the host.</param>
    internal sealed record Request(
        ScenarioDefinition Definition,
        ScenarioNode Node,
        StepMessage Title,
        IReadOnlyList<TeardownRegistration> Cleanups,
        bool ScenarioSucceeded,
        DateTimeOffset StartedAt,
        bool SimulatedTime,
        IServiceProvider? Services,
        MessageFormattingContext Formatting,
        IStepInputs Inputs,
        ScenarioScheduler.ScenarioStart ScenarioStart,
        Func<string, Task>? BeforeTermination);

    public async Task<StepResult> RunAsync(Request request)
    {
        var (definition, node, titleMessage, ordered, succeeded, startedAt, simulatedTime, services, messageFormatting, inputs, scenarioStart, beforeTermination) = request;
        var nodes = definition.Nodes;
        var name = titleMessage.Text;

        var optionalAllowed = definition.TeardownPolicy switch
        {
            Run.Always => true,
            Run.OnSuccess => succeeded,
            _ => false,
        };

        // The STATUS answers one question — did teardown succeed? Whether the policy skipped
        // anything is information, not a verdict, so it goes in the log. Otherwise every suite
        // that never uses teardown, and every deliberate Run.Never, would carry a non-passing node.
        //
        // One context for the teardown node. Cleanups log and attach through it — the step that
        // registered them has long since been reported — and it is the ambient
        // ScenarioContext.Current while they run, so domain code below a cleanup attributes its
        // lines here. Callback views get fresh independent deadline tokens, so cancellation of
        // the scenario cannot suppress cleanup. It is NOT attached to the conflict ledger: teardown runs after every other node is
        // terminal, so it is ordered after all of them, and a cleanup's Delete must never be refused
        // as a conflict with the step that created the thing.
        var teardownContext = new ScenarioContext(
            node.StepId, name, services, messageFormatting.ForStep(node.Index), timeProvider, CancellationToken.None);
        foreach (var note in StepNotes.FormatMessages(node, messageFormatting, inputs))
        {
            teardownContext.AddNote(note);
        }
        teardownContext.AttachScenario(definition.Info);
        teardownContext.AttachScenarioStart(scenarioStart.Of(startedAt));
        using var teardownActivity = ScenarioScheduler.StartStepActivity(definition, node, name);
        teardownContext.AttachActivity(teardownActivity);
        var skipped = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        List<Exception>? errors = null;

        using var cleanupWatchdog = new CancellationWatchdog(cancellationGracePeriod, CancellationToken.None, beforeTermination: beforeTermination);
        ScenarioContext.SetCurrent(teardownContext);
        try
        {
            foreach (var entry in ordered)
            {
                var owner = nodes[entry.OwningStepIndex].OperationName;
                if (entry.Kind == Cleanup.Optional && !optionalAllowed)
                {
                    skipped.Add(owner);
                    continue;
                }

#pragma warning disable CA2000 // This using owns the CTS; its nested watchdog handle drains callbacks before disposal.
                using (var cleanupCts = new CancellationTokenSource())
#pragma warning restore CA2000
                {
                    var identity = $"cleanup registration #{entry.Sequence} from step '{owner}' in scenario '{definition.DisplayName}'";
                    using var cleanupWatch = cleanupWatchdog.Watch(cleanupCts, cleanupTimeout, identity);
                    var callbackContext = teardownContext.WithCancellationToken(cleanupCts.Token);
                    ScenarioContext.SetCurrent(callbackContext);
                    try
                    {
                        try
                        {
                            await entry.Cleanup(callbackContext).ConfigureAwait(false);
                        }
                        finally
                        {
                            await cleanupWatch.CompleteAsync().ConfigureAwait(false);
                        }
                        if (cleanupWatch.TimedOut)
                        {
                            throw new TimeoutException($"{identity} exceeded its timeout of {cleanupTimeout}.");
                        }
                        teardownContext.Log($"cleaned up: {owner}");
                    }
                    catch (Exception ex)
                    {
                        // Advance only after actual callback/cancellation work has stopped.
                        Exception error = cleanupWatch.TimedOut
                            ? new TimeoutException($"{identity} exceeded its timeout of {cleanupTimeout}.", ex)
                            : new InvalidOperationException($"{identity} failed: {ex.Message}", ex);
                        (errors ??= []).Add(error);
                        teardownContext.Log($"cleanup FAILED: {owner} [registration #{entry.Sequence}] — {error.Message}");
                    }
                    finally
                    {
                        await cleanupWatch.CompleteAsync().ConfigureAwait(false);
                        ScenarioContext.SetCurrent(teardownContext);
                    }
                }
            }
        }
        finally
        {
            ScenarioContext.SetCurrent(null);
        }

        stopwatch.Stop();

        if (ordered.Count == 0)
        {
            teardownContext.Log("no cleanup registered");
        }

        if (skipped.Count > 0)
        {
            var why = $"teardown policy is {definition.TeardownPolicy}"
                + (definition.TeardownPolicy == Run.OnSuccess && !succeeded
                    ? " and the scenario failed"
                    : string.Empty);
            teardownContext.Log($"skipped {skipped.Count} optional cleanup(s) — {why}: {string.Join(", ", skipped)}");
        }

        if (teardownActivity is not null)
        {
            teardownActivity.SetTag(RaunTelemetry.Attributes.TestCaseResultStatus, errors is null ? "pass" : "fail");
            if (errors is not null)
            {
                teardownActivity.SetStatus(ActivityStatusCode.Error, $"{errors.Count} teardown action(s) failed.");
                foreach (var error in errors)
                {
                    teardownActivity.AddException(error);
                }
            }
        }

        return new StepResult
        {
            Node = node,
            TitleMessage = titleMessage,
            Status = errors is null ? StepStatus.Passed : StepStatus.Failed,
            StartedAt = startedAt,

            // A simulated timeline is made of what steps declare; a cleanup declares nothing.
            Duration = simulatedTime ? TimeSpan.Zero : stopwatch.Elapsed,
            TraceId = teardownActivity?.TraceId.ToString(),
            SpanId = teardownActivity?.SpanId.ToString(),
            NoteMessages = teardownContext.NoteMessages,
            LogEntries = teardownContext.LogEntries,
            Attachments = teardownContext.Attachments,
            Effects = teardownContext.Resources.Effects,
            Links = teardownContext.Resources.Links,
            Exception = errors is null
                ? null
                : new AggregateException($"{errors.Count} teardown action(s) failed.", errors),
        };
    }
}
