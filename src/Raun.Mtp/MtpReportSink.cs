using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.TestHost;
using Raun.Model;
using Raun.Reporting;

namespace Raun.Mtp;

/// <summary>
/// Session-scoped sink that bridges the run-event stream onto the Microsoft.Testing.Platform message
/// bus: one <see cref="TestNodeUpdateMessage"/> per step lifecycle event, so each scenario step is a
/// first-class MTP node. Replaces the per-scenario <c>RaunStepReporter</c>; identical messages, but
/// keyed off the <see cref="ScenarioDefinition"/> carried on each event so one instance serves the
/// whole run. The step-numbering labels are computed once per scenario (on <see cref="ScenarioStarted"/>)
/// and cached by <see cref="ScenarioDefinition.ScenarioId"/>.
/// </summary>
internal sealed class MtpReportSink : RunEventSink
{
    private readonly SessionUid _sessionUid;
    private readonly IMessageBus _messageBus;
    private readonly IDataProducer _producer;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<int, string>> _labels =
        new(StringComparer.Ordinal);

    public MtpReportSink(SessionUid sessionUid, IMessageBus messageBus, IDataProducer producer)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
        ArgumentNullException.ThrowIfNull(producer);
        _sessionUid = sessionUid;
        _messageBus = messageBus;
        _producer = producer;
    }

    protected override ValueTask OnScenarioStartedAsync(ScenarioStarted e)
    {
        _labels[e.Definition.ScenarioId] = StepNumbering.Compute(e.Definition);
        return default;
    }

    protected override async ValueTask OnStepStartedAsync(StepStarted e)
    {
        if (e.Context.Node.IsSynthetic)
        {
            return;
        }

        var testNode = BuildNode(e.Definition, e.Context.Node, e.Context.DisplayName);
        testNode.Properties.Add(InProgressTestNodeStateProperty.CachedInstance);
        await Publish(testNode).ConfigureAwait(false);
    }

    protected override async ValueTask OnStepFinishedAsync(StepFinished e)
    {
        var result = e.Result;
        if (result.Node.IsSynthetic)
        {
            return;
        }

        // A not-taken branch is reported as skipped with its "not taken: …" reason (see MapState), so
        // the console tally stays honest: the step exists, it did not run, and here is why. The
        // scheduler raises no OnStepStarting for it, so the node goes straight from discovered to
        // skipped — the same shape any runner's own skipped test has. Before 2026-09-05 it received
        // no terminal state at all and silently vanished from the count.
        var testNode = BuildNode(e.Definition, result.Node, result.DisplayName);
        testNode.Properties.Add(MapState(result));

        // Absolute window from the scheduler-stamped StartedAt (design §3.B bonus): accurate even
        // for concurrent steps, replacing the old finish-anchored (UtcNow - Duration) approximation.
        testNode.Properties.Add(new TimingProperty(
            new TimingInfo(result.StartedAt, result.StartedAt + result.Duration, result.Duration)));

        AddOutput(testNode, result);
        AddAttachments(testNode, e.Definition, result);
        await Publish(testNode).ConfigureAwait(false);
    }

    private TestNode BuildNode(ScenarioDefinition definition, ScenarioNode node, string displayName)
    {
        var labels = _labels.TryGetValue(definition.ScenarioId, out var cached)
            ? cached
            : StepNumbering.Compute(definition); // defensive: step before its ScenarioStarted

        var testNode = new TestNode
        {
            Uid = StepUid.Of(definition, node),
            DisplayName = StepNumbering.Format(labels, node, displayName),
        };

        testNode.Properties.Add(ScenarioTestIdentity.Create(definition));

        if (!string.IsNullOrEmpty(node.SourceFile) && node.SourceLine > 0)
        {
            var position = new LinePosition(node.SourceLine, 0);
            testNode.Properties.Add(new TestFileLocationProperty(
                node.SourceFile, new LinePositionSpan(position, position)));
        }

        return testNode;
    }

    // --- copied verbatim from RaunStepReporter ---

    /// <summary>Maps a terminal <see cref="StepResult"/> onto its MTP node-state property (design §6).</summary>
    private static IProperty MapState(StepResult result) => result.Status switch
    {
        StepStatus.Passed => PassedTestNodeStateProperty.CachedInstance,
        StepStatus.Skipped => new SkippedTestNodeStateProperty(result.SkipReason ?? "skipped"),
        StepStatus.NotTaken => new SkippedTestNodeStateProperty(result.SkipReason ?? "not taken"),
        StepStatus.Failed => MapFailure(result.Exception),
        // Pending/Running are not terminal; the scheduler never reports them as a finished result.
        _ => new ErrorTestNodeStateProperty(
            new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "Unexpected terminal step status '{0}'.", result.Status))),
    };

    private static IProperty MapFailure(Exception? exception)
    {
        var ex = exception ?? new InvalidOperationException("Step failed without an exception.");
        return ex switch
        {
            TimeoutException => new TimeoutTestNodeStateProperty(ex),
            _ when IsAssertionException(ex) => new FailedTestNodeStateProperty(ex),
            _ => new ErrorTestNodeStateProperty(ex),
        };
    }

    /// <summary>
    /// Recognizes an assertion failure without taking a compile-time dependency on any assertion
    /// library (Raun.Mtp references only MTP + Raun core). xunit.v3.assert raises
    /// <c>Xunit.Sdk.XunitException</c>; Shouldly/NUnit/etc. raise types whose name or interface ends
    /// in <c>AssertionException</c>. Anything else is treated as an unexpected error.
    /// </summary>
    private static bool IsAssertionException(Exception exception)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (type.Name == "XunitException" || type.Name.EndsWith("AssertionException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var iface in exception.GetType().GetInterfaces())
        {
            if (iface.Name.EndsWith("AssertionException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddOutput(TestNode testNode, StepResult result)
    {
        if (result.Logs.Count == 0 && result.TraceId is null)
        {
            return;
        }

        // One line per log entry, in the order they were written, each prefixed with the time since
        // the scenario started ("+1.234s message"). Resource events are already part of the stream
        // ("[resource] Create User:jane"), so nothing is appended after the logs. A result built
        // without offsets (outside the scheduler) prints its plain lines. When a trace listener was
        // subscribed, the last line names the trace so it can be pasted into a trace viewer.
        var lines = result.LogEntries.Count > 0
            ? result.LogEntries.Select(e => e.ToString())
            : result.Logs;
        if (result.TraceId is not null)
        {
            lines = lines.Append($"[trace] {result.TraceId} span {result.SpanId}");
        }

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }

        testNode.Properties.Add(new StandardOutputProperty(builder.ToString()));
    }

    private static void AddAttachments(TestNode testNode, ScenarioDefinition definition, StepResult result)
    {
        if (result.Attachments.Count == 0)
        {
            return;
        }

        // Mirror xUnit's MTP sink: persist string attachments to temp files and surface them as
        // file artifacts so runners/TRX can collect them. Best-effort — a failed write must not
        // abort reporting.
        string? basePath = null;
        foreach (var (name, value) in result.Attachments)
        {
            try
            {
                basePath ??= CreateAttachmentDirectory(definition, result);
                var path = Path.Combine(basePath, SanitizeFileName(name) + ".txt");
                File.WriteAllText(path, value);
                testNode.Properties.Add(new FileArtifactProperty(new FileInfo(path), name));
            }
            catch (IOException)
            {
                // Skip this attachment; keep reporting the rest of the node.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string CreateAttachmentDirectory(ScenarioDefinition definition, StepResult result)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "raun-mtp",
            SanitizeFileName(StepUid.Of(definition, result.Node)));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        return builder.ToString();
    }

    private Task Publish(TestNode testNode)
    {
        NodeDiagnostics.Log("run", testNode);
        return _messageBus.PublishAsync(_producer, new TestNodeUpdateMessage(_sessionUid, testNode));
    }
}
