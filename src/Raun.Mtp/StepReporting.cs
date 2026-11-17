using System.Globalization;
using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Model;

namespace Raun.Mtp;

/// <summary>
/// How one <see cref="StepResult"/> translates to Microsoft.Testing.Platform terms: its node state,
/// its output lines and its attachments. Shared by the per-step sink (<see cref="MtpReportSink"/>)
/// and the per-scenario one (<see cref="ScenarioMtpReportSink"/>) so a step means the same thing
/// whichever of them reports it.
/// </summary>
internal static class StepReporting
{
    /// <summary>Maps a terminal <see cref="StepResult"/> onto its MTP node-state property (design §6).</summary>
    public static IProperty MapState(StepResult result) => result.Status switch
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
    /// The exception type names assertion libraries raise, matched as suffixes so a namespace never
    /// matters. Each one is a library Raun cannot reference (Raun.Mtp depends on MTP + Raun core and
    /// nothing else), so the names are the whole contract: <c>XunitException</c> (xunit.v3.assert),
    /// <c>AssertionException</c> (NUnit and others), <c>AssertionFailedException</c>
    /// (FluentAssertions), <c>ShouldAssertException</c> (Shouldly), <c>AssertFailedException</c>
    /// (MSTest). A library not listed here reports as an error rather than a failure — annoying, but
    /// never wrong about whether the test passed.
    /// </summary>
    private static readonly string[] AssertionExceptionNames =
    [
        "XunitException",
        "AssertionException",
        "AssertionFailedException",
        "ShouldAssertException",
        "AssertFailedException",
    ];

    /// <summary>
    /// Recognizes an assertion failure without taking a compile-time dependency on any assertion
    /// library, by walking the exception's base types and its interfaces and matching their names
    /// against <see cref="AssertionExceptionNames"/>. Anything else is an unexpected error.
    /// </summary>
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

    /// <summary>
    /// The step's output, one line per log entry in the order written, each prefixed with the time
    /// since the scenario started ("+1.234s message"). Resource events are already part of the stream
    /// ("[resource] Create User:jane"), so nothing is appended after the logs. A result built without
    /// offsets (outside the scheduler) yields its plain lines. When a trace listener was subscribed,
    /// the last line names the trace so it can be pasted into a trace viewer. Empty when the step
    /// wrote nothing.
    /// </summary>
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

    /// <summary>
    /// Mirrors xUnit's MTP sink: persists the step's string attachments under
    /// <paramref name="attachmentRoot"/> and surfaces them on <paramref name="testNode"/> as file
    /// artifacts so runners/TRX can collect them. Best-effort — a failed write must not abort
    /// reporting.
    /// </summary>
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
                // Skip this attachment; keep reporting the rest of the node.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
