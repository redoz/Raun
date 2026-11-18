using System.Globalization;
using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Model;

namespace Raun.Mtp;

internal static class StepReporting
{
    public static IProperty MapState(StepResult result) => result.Status switch
    {
        StepStatus.Passed => PassedTestNodeStateProperty.CachedInstance,
        StepStatus.Skipped => new SkippedTestNodeStateProperty(result.SkipReason ?? "skipped"),
        StepStatus.NotTaken => new SkippedTestNodeStateProperty(result.SkipReason ?? "not taken"),
        StepStatus.Failed => MapFailure(result),
        _ => new ErrorTestNodeStateProperty(
            new InvalidOperationException(
                string.Format(CultureInfo.InvariantCulture, "Unexpected terminal step status '{0}'.", result.Status))),
    };

    // The runtime says what kind of failure it is; this only spells that in the platform's states.
    private static IProperty MapFailure(StepResult result)
    {
        var ex = result.Exception ?? new InvalidOperationException("Step failed without an exception.");
        return result.FailureKind switch
        {
            StepFailureKind.Timeout => new TimeoutTestNodeStateProperty(ex),
            StepFailureKind.Assertion => new FailedTestNodeStateProperty(ex),
            _ => new ErrorTestNodeStateProperty(ex),
        };
    }

    /// <summary>Where a step is declared, for "go to source"; null when the generator recorded no
    /// file or line (<see cref="ScenarioNode.SourceLine"/> is 0 when unknown).</summary>
    public static TestFileLocationProperty? FileLocation(ScenarioNode node)
    {
        if (string.IsNullOrEmpty(node.SourceFile) || node.SourceLine <= 0)
        {
            return null;
        }

        var position = new LinePosition(node.SourceLine, 0);
        return new TestFileLocationProperty(node.SourceFile, new LinePositionSpan(position, position));
    }

    public static IEnumerable<string> OutputLines(StepResult result)
    {
        var lines = result.LogEntries.Select(e => e.ToString());
        return result.TraceId is null
            ? lines
            : lines.Append($"[trace] {result.TraceId} span {result.SpanId}");
    }

    public static void AddAttachments(TestNode testNode, string attachmentRoot, ScenarioDefinition definition, StepResult result)
    {
        if (result.Attachments.Count == 0)
        {
            return;
        }

        string? basePath = null;
        foreach (var (name, value) in result.Attachments)
        {
            try
            {
                if (basePath is null)
                {
                    basePath = Path.Combine(
                        attachmentRoot, RaunAttachments.SanitizeFileName(StepUid.Of(definition, result.Node)));
                    Directory.CreateDirectory(basePath);
                }

                var path = Path.Combine(basePath, RaunAttachments.SanitizeFileName(name) + ".txt");
                File.WriteAllText(path, value);
                testNode.Properties.Add(new FileArtifactProperty(new FileInfo(path), name));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
