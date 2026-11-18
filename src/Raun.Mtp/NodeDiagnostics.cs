using System.Globalization;
using System.Text;
using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Diagnostics;

namespace Raun.Mtp;

/// <summary>
/// Opt-in tracing of every <see cref="TestNode"/> Raun publishes to the MTP message bus (discovery
/// and each step lifecycle update). Off by default and zero-cost; set <c>RAUN_NODE_DEBUG</c> to a
/// file path, or to <c>1</c>/<c>stderr</c>/<c>console</c> for stderr. Use it to see the exact Uid,
/// display name, state, and property bag of each node — in particular to compare the in-progress
/// node (which a runner groups) against the finished node (which can collapse) when diagnosing how a
/// runner builds its tree.
/// </summary>
internal static class NodeDiagnostics
{
    /// <summary>Whether tracing is enabled (the <c>RAUN_NODE_DEBUG</c> environment variable is set).</summary>
    public static bool Enabled => DebugTrace.Enabled;

    /// <summary>Traces one published node under the given <paramref name="phase"/> (e.g. "discover", "run").</summary>
    public static void Log(string phase, TestNode node)
    {
        if (!Enabled)
        {
            return;
        }

        DebugTrace.Write(phase, Describe(node));
    }

    /// <summary>Traces a plain message under the given <paramref name="phase"/> (e.g. "report-sink-failure").</summary>
    public static void Log(string phase, string message) => DebugTrace.Write(phase, message);

    private static string Describe(TestNode node)
    {
        var state = node.Properties.OfType<TestNodeStateProperty>().FirstOrDefault()?.GetType().Name ?? "(none)";

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"uid='{node.Uid.Value}' name='{node.DisplayName}' state={state}");

        var identity = node.Properties.OfType<TestMethodIdentifierProperty>().FirstOrDefault();
        if (identity is not null)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $" identity=(asm='{identity.AssemblyFullName}' ns='{identity.Namespace}' type='{identity.TypeName}' method='{identity.MethodName}')");
        }
        else
        {
            builder.Append(" identity=(none)");
        }

        var props = string.Join(",", node.Properties.AsEnumerable().Select(p => p.GetType().Name));
        builder.Append(CultureInfo.InvariantCulture, $" count={node.Properties.Count} props=[{props}]");

        return builder.ToString();
    }
}
