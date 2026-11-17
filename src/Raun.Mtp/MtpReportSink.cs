using System.Collections.Concurrent;
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
    private readonly string _attachmentRoot;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<int, string>> _labels =
        new(StringComparer.Ordinal);

    /// <summary>Creates the sink. <paramref name="attachmentRoot"/> is where step attachments are
    /// written before being published as file artifacts; see <see cref="RaunAttachments"/>.</summary>
    public MtpReportSink(SessionUid sessionUid, IMessageBus messageBus, IDataProducer producer, string attachmentRoot)
    {
        ArgumentNullException.ThrowIfNull(messageBus);
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentException.ThrowIfNullOrEmpty(attachmentRoot);
        _sessionUid = sessionUid;
        _messageBus = messageBus;
        _producer = producer;
        _attachmentRoot = attachmentRoot;
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
        testNode.Properties.Add(StepReporting.MapState(result));

        // Absolute window from the scheduler-stamped StartedAt (design §3.B bonus): accurate even
        // for concurrent steps, replacing the old finish-anchored (UtcNow - Duration) approximation.
        testNode.Properties.Add(new TimingProperty(
            new TimingInfo(result.StartedAt, result.StartedAt + result.Duration, result.Duration)));

        var output = string.Concat(StepReporting.OutputLines(result).Select(line => line + Environment.NewLine));
        if (output.Length > 0)
        {
            testNode.Properties.Add(new StandardOutputProperty(output));
        }

        StepReporting.AddAttachments(testNode, _attachmentRoot, e.Definition, result);
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
        foreach (var tag in ScenarioNodePath.Tags(definition))
        {
            testNode.Properties.Add(tag);
        }

        if (!string.IsNullOrEmpty(node.SourceFile) && node.SourceLine > 0)
        {
            var position = new LinePosition(node.SourceLine, 0);
            testNode.Properties.Add(new TestFileLocationProperty(
                node.SourceFile, new LinePositionSpan(position, position)));
        }

        return testNode;
    }

    private Task Publish(TestNode testNode)
    {
        NodeDiagnostics.Log("run", testNode);
        return _messageBus.PublishAsync(_producer, new TestNodeUpdateMessage(_sessionUid, testNode));
    }
}
