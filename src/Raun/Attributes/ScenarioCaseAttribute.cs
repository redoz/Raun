namespace Raun;

/// <summary>Supplies one independently discovered set of constant inputs to a scenario.</summary>
/// <remarks>Case names are stable, case-sensitive identities within the originating method.</remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ScenarioCaseAttribute : Attribute
{
    public ScenarioCaseAttribute(string caseName, params object?[] arguments)
    {
        CaseName = caseName;
        Arguments = Array.AsReadOnly(arguments ?? [null]);
    }

    public string CaseName { get; }

    public IReadOnlyList<object?> Arguments { get; }
}
