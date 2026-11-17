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
        StepStatus.Failed => MapFailure(result.Exception),
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
    /// <c>XunitException</c> (xunit.v3.assert), <c>AssertionException</c> (NUnit and others),
    /// <c>AssertionFailedException</c> (FluentAssertions), <c>ShouldAssertException</c> (Shouldly),
    /// <c>AssertFailedException</c> (MSTest).
    /// </summary>
    private static readonly string[] AssertionExceptionNames =
    [
        "XunitException",
        "AssertionException",
        "AssertionFailedException",
        "ShouldAssertException",
        "AssertFailedException",
    ];

    private static bool IsAssertionException(Exception exception)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (IsAssertionName(type.Name))
            {
                return true;
            }
        }

        foreach (var iface in exception.GetType().GetInterfaces())
        {
            if (IsAssertionName(iface.Name))
            {
                return true;
            }
        }

        return false;

        static bool IsAssertionName(string name)
        {
            foreach (var known in AssertionExceptionNames)
            {
                if (name.EndsWith(known, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
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
        if (result.Logs.Count == 0 && result.TraceId is null)
        {
            return [];
        }

        var lines = result.LogEntries.Count > 0
            ? result.LogEntries.Select(e => e.ToString())
            : result.Logs;
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
