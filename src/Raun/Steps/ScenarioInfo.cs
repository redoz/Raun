using System.Reflection;

namespace Raun;

/// <summary>
/// The scenario a context belongs to. A world reads its per-scenario settings from here — typically an
/// attribute of your own on the scenario method: <c>context.Scenario.Method?.GetCustomAttribute&lt;SeedAttribute&gt;()</c>.
/// </summary>
/// <param name="Id">The scenario's stable id: the same on every run and every machine.</param>
/// <param name="DisplayName">The scenario's display name.</param>
/// <param name="MethodName">The full name of the <c>[Scenario]</c> method.</param>
/// <param name="Method">The <c>[Scenario]</c> method, when the generator recorded it (it does for a
/// scenario with a world); null otherwise.</param>
public sealed record ScenarioInfo(string Id, string DisplayName, string MethodName, MethodInfo? Method)
{
    /// <summary>The scenario of a context built outside a run, as a DSL method under unit test.</summary>
    public static ScenarioInfo Unknown { get; } = new("", "", "", null);
}
