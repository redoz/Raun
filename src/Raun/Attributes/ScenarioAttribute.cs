using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Raun;

/// <summary>
/// Marks a static method as a Raun scenario. The method body is source for the Raun generator
/// (it is never executed directly); Raun's own Microsoft.Testing.Platform test framework discovers,
/// runs, and reports each Given/When/Then step as its own MTP test node.
/// </summary>
/// <remarks>
/// <para>
/// This replaces the xUnit-coupled attribute that used to live in <c>Raun.Xunit</c> (which derived
/// from <c>FactAttribute</c> so xUnit's attribute discovery could find it). Discovery is Raun's own
/// now, so this is a plain marker attribute with no test-runner coupling, and it lives in the core
/// so a scenario can be authored against <c>Raun</c> alone. It stays in namespace <c>Raun</c> so
/// authoring is just <c>using Raun;</c>.
/// </para>
/// <para>
/// The generator matches it by metadata name (<c>Raun.ScenarioAttribute</c>) and reads the
/// display-name constructor argument plus the <see cref="Timeout"/> named argument; the per-step
/// source locations come from the method's syntax tree, so the
/// <see cref="CallerFilePathAttribute"/> / <see cref="CallerLineNumberAttribute"/> parameters are
/// retained only for source-compatibility with existing scenarios.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[SuppressMessage("Design", "CA1019:Define accessors for attribute arguments", Justification = "The display-name positional argument is exposed via the intentionally-named ScenarioDisplayName property (the name the source generator and the original Raun.Xunit attribute used); the caller-info arguments are surfaced via SourceFilePath/SourceLineNumber.")]
public sealed class ScenarioAttribute : Attribute
{
    /// <summary>Creates the attribute, optionally naming the scenario for the test runner.</summary>
    /// <param name="displayName">Optional scenario display name; the generator reads it to name the scenario.</param>
    /// <param name="sourceFilePath">Captured automatically; retained for source compatibility.</param>
    /// <param name="sourceLineNumber">Captured automatically; retained for source compatibility.</param>
    public ScenarioAttribute(
        string? displayName = null,
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
    {
        ScenarioDisplayName = displayName;
        SourceFilePath = sourceFilePath;
        SourceLineNumber = sourceLineNumber;
    }

    /// <summary>Optional scenario display name; the generator reads it to name the scenario.</summary>
    public string? ScenarioDisplayName { get; }

    /// <summary>The source file the scenario was declared in (captured at the call site).</summary>
    public string? SourceFilePath { get; }

    /// <summary>The source line the scenario was declared on (captured at the call site).</summary>
    public int SourceLineNumber { get; }

    /// <summary>Optional per-scenario timeout in milliseconds; the generator reads this named argument.</summary>
    public int Timeout { get; set; }
}
