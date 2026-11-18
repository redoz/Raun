namespace Raun.Diagnostics;

/// <summary>
/// Opt-in tracing of what Raun does around a run, for diagnosing Raun itself: which nodes an adapter
/// published, why a report or a coverage analysis was not produced. Off by default and zero-cost; set
/// <c>RAUN_NODE_DEBUG</c> to a file path, or to <c>1</c>/<c>stderr</c>/<c>console</c> for standard
/// error. One writer for the process, so lines from the runtime, an adapter and an extension do not
/// overwrite one another in the file.
/// </summary>
public static class DebugTrace
{
    private static readonly string? Sink = Environment.GetEnvironmentVariable("RAUN_NODE_DEBUG");
    private static readonly object FileLock = new();

    /// <summary>Whether tracing is on (the <c>RAUN_NODE_DEBUG</c> environment variable is set).</summary>
    public static bool Enabled => !string.IsNullOrEmpty(Sink);

    /// <summary>Traces one line: <c>[raun-node] {phase} {message}</c>. Does nothing when tracing is
    /// off, and never throws: tracing must not break a run.</summary>
    public static void Write(string phase, string message)
    {
        if (!Enabled)
        {
            return;
        }

        var line = $"[raun-node] {phase} {message}";
        if (string.Equals(Sink, "1", StringComparison.Ordinal)
            || string.Equals(Sink, "stderr", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Sink, "console", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(line);
            return;
        }

        try
        {
            lock (FileLock)
            {
                File.AppendAllText(Sink!, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Diagnostics are best-effort; never let tracing break a run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
