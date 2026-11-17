using System.Text;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.TestHost;
using Raun.Model;
using Raun.Reporting;

namespace Raun.Mtp;

/// <summary>
/// The sink for <see cref="TestGranularity.Scenario"/>: bridges the run-event stream onto the
/// Microsoft.Testing.Platform message bus as one test node per scenario — in progress when the
/// scenario starts, and one terminal state folded from its steps when it finishes. Steps publish
/// nothing of their own; they appear as lines in the scenario's output.
/// </summary>
/// <remarks>
/// The fold, over the scenario's real (non-synthetic) steps in the order they started:
/// a timed-out step makes the scenario timed out; otherwise the first failed step decides between
/// failed and error exactly as it would for its own node; otherwise one passed step makes the
/// scenario passed; and a scenario in which nothing ran is skipped. A branch that was not taken, or
/// a step skipped along a path that otherwise passed, never counts against the scenario.
/// </remarks>
internal sealed class ScenarioMtpReportSink : RunEventSink
{
    private readonly SessionUid _sessionUid;
    private readonly IMessageBus _messageBus;
    private readonly IDataProducer _producer;
    private readonly string _attachmentRoot;

    /// <summary>Creates the sink. <paramref name="attachmentRoot"/> is where step attachments are
    /// written before being published as file artifacts; see <see cref="RaunAttachments"/>.</summary>
    public ScenarioMtpReportSink(SessionUid sessionUid, IMessageBus messageBus, IDataProducer producer, string attachmentRoot)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentException.ThrowIfNullOrEmpty(attachmentRoot);
        _sessionUid = sessionUid;
        _messageBus = messageBus;
        _producer = producer;
        _attachmentRoot = attachmentRoot;
    }

    protected override async ValueTask OnScenarioStartedAsync(ScenarioStarted e)
    {
        var testNode = BuildNode(e.Definition);
        testNode.Properties.Add(InProgressTestNodeStateProperty.CachedInstance);
        await Publish(testNode).ConfigureAwait(false);
    }

    protected override async ValueTask OnScenarioFinishedAsync(ScenarioFinished e)
    {
        // Stable sort: steps stamped with the same instant keep the order the scheduler reported.
        var steps = e.Results
            .Where(r => !r.Node.IsSynthetic)
            .OrderBy(r => r.StartedAt)
            .ToList();

        var testNode = BuildNode(e.Definition);
        testNode.Properties.Add(Fold(steps));

        if (steps.Count > 0)
        {
            var start = steps[0].StartedAt;
            var end = steps.Max(r => r.StartedAt + r.Duration);
            testNode.Properties.Add(new TimingProperty(new TimingInfo(start, end, end - start)));

            testNode.Properties.Add(new StandardOutputProperty(Describe(e.Definition, steps)));
            foreach (var step in steps)
            {
                StepReporting.AddAttachments(testNode, _attachmentRoot, e.Definition, step);
            }
        }

        await Publish(testNode).ConfigureAwait(false);
    }

    private static IProperty Fold(List<StepResult> steps)
    {
        IProperty? failure = null;
        var passed = false;
        string? skipReason = null;

        foreach (var step in steps)
        {
            switch (step.Status)
            {
                case StepStatus.Passed:
                    passed = true;
                    break;
                case StepStatus.Skipped:
                case StepStatus.NotTaken:
                    skipReason ??= step.SkipReason;
                    break;
                default:
                    var state = StepReporting.MapState(step);
                    if (state is TimeoutTestNodeStateProperty)
                    {
                        return state;
                    }

                    failure ??= state;
                    break;
            }
        }

        return failure
            ?? (passed
                ? PassedTestNodeStateProperty.CachedInstance
                : new SkippedTestNodeStateProperty(skipReason ?? "skipped"));
    }

    /// <summary>One header line per step — its number, name and outcome — followed by what the step
    /// wrote, indented. This is where a reader finds which step failed a scenario.</summary>
    private static string Describe(ScenarioDefinition definition, List<StepResult> steps)
    {
        var labels = StepNumbering.Compute(definition);
        var builder = new StringBuilder();
        foreach (var step in steps)
        {
            builder.Append(StepNumbering.Format(labels, step.Node, step.DisplayName));
            builder.Append(" [").Append(Outcome(step)).AppendLine("]");
            foreach (var line in StepReporting.OutputLines(step))
            {
                builder.Append("  ").AppendLine(line);
            }
        }

        return builder.ToString();
    }

    private static string Outcome(StepResult step) => step.Status switch
    {
        StepStatus.Passed => "passed",
        StepStatus.Failed => "failed",
        StepStatus.Skipped => step.SkipReason is null ? "skipped" : "skipped: " + step.SkipReason,
        StepStatus.NotTaken => step.SkipReason ?? "not taken",
        _ => step.Status.ToString(),
    };

    private static TestNode BuildNode(ScenarioDefinition definition)
    {
        var testNode = new TestNode
        {
            Uid = definition.ScenarioId,
            DisplayName = definition.DisplayName,
        };

        testNode.Properties.Add(ScenarioTestIdentity.Create(definition));
        foreach (var tag in ScenarioNodePath.Tags(definition))
        {
            testNode.Properties.Add(tag);
        }

        return testNode;
    }

    private Task Publish(TestNode testNode)
    {
        NodeDiagnostics.Log("run", testNode);
        return _messageBus.PublishAsync(_producer, new TestNodeUpdateMessage(_sessionUid, testNode));
    }
}
