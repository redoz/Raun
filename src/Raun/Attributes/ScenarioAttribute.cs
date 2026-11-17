namespace Raun;

/// <summary>
/// Marks a static method as a Raun scenario. The method body is source for the Raun generator
/// (it is never executed directly); Raun's own Microsoft.Testing.Platform test framework discovers,
/// runs, and reports each Given/When/Then step as its own MTP test node.
/// </summary>
/// <remarks>
/// A plain marker with no test-runner coupling, in the core so a scenario can be authored against
/// <c>Raun</c> alone. The generator matches it by metadata name (<c>Raun.ScenarioAttribute</c>) and
/// reads the display-name constructor argument plus the <see cref="Timeout"/> named argument; source
/// locations come from the method's syntax tree.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ScenarioAttribute : Attribute
{
    /// <summary>Creates the attribute, optionally naming the scenario for the test runner.</summary>
    /// <param name="displayName">Optional scenario display name; the generator reads it to name the scenario.</param>
    public ScenarioAttribute(string? displayName = null) => DisplayName = displayName;

    /// <summary>Optional scenario display name; the generator reads it to name the scenario.</summary>
    public string? DisplayName { get; }

    /// <summary>Optional per-scenario timeout in milliseconds; the generator reads this named argument.</summary>
    public int Timeout { get; set; }
}
