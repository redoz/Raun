using System.Text;
using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.TestHost;
using Raun.Model;
using Raun.Reporting;

namespace Raun.Mtp;

internal sealed class ScenarioMtpReportSink : RunEventSink
{
    private readonly SessionUid _sessionUid;
    private readonly IMessageBus _messageBus;
    private readonly IDataProducer _producer;
    private readonly string _attachmentRoot;

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
        var steps = e.Results
            .Where(r => !r.Node.IsSynthetic)
            .OrderBy(r => r.StartedAt)
            .ThenBy(r => r.Node.Index)
            .ToList();

        var testNode = BuildNode(e.Definition);
        testNode.Properties.Add(Fold(steps));

        if (steps.Count > 0)
        {
            var windows = steps
                .Where(r => r.StartedAt is not null)
                .Select(r => (Start: r.StartedAt!.Value, End: r.StartedAt!.Value + r.Duration))
                .ToList();
            if (windows.Count > 0)
            {
                var start = windows.Min(w => w.Start);
                var end = windows.Max(w => w.End);
                testNode.Properties.Add(new TimingProperty(new TimingInfo(start, end, end - start)));
            }

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
