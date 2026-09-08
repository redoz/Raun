using System.Reflection;
using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Model;

namespace Raun.Mtp;

/// <summary>
/// Derives a <see cref="TestMethodIdentifierProperty"/> from a scenario's fully-qualified method
/// name so runners (VS Test Explorer, the VSTest bridge) group step nodes under their real
/// namespace → class → scenario-method. Without this property a node has no method identity and the
/// runner buckets it under <c>&lt;Empty Namespace&gt;</c> / <c>&lt;Empty Class&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Namespace and type come from <see cref="ScenarioIdentity.Resolve"/>: the members the generator
/// recorded on the definition, or the dotted split of <see cref="ScenarioDefinition.MethodName"/>
/// for a definition that predates them.
/// </para>
/// <para>
/// The assembly name is resolved once from the entry assembly (the running test app), matching what
/// xunit.v3's MTP bridge stamps; it is metadata only and does not affect node identity (the
/// <c>{ScenarioId}:{StepId}</c> uid does).
/// </para>
/// </remarks>
internal static class ScenarioTestIdentity
{
    private static readonly string AssemblyFullName = Assembly.GetEntryAssembly()?.FullName ?? string.Empty;

    /// <summary>The VSTest spelling of a <see langword="void"/> return type (matches xunit.v3's MTP bridge).</summary>
    private const string VoidReturnTypeName = "System.Void";

    /// <summary>
    /// Builds the method-identity property for a scenario: namespace and type come from the
    /// definition (see <see cref="ScenarioIdentity.Resolve"/>), but the method node is the human scenario display
    /// name so a runner groups steps under the scenario name. A non-empty
    /// <see cref="ScenarioDefinition.ClassDisplayName"/> overrides the type name.
    /// </summary>
    public static TestMethodIdentifierProperty Create(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ScenarioIdentity.Resolve(definition, out var @namespace, out var typeName);
        return Create(@namespace, typeName, definition.DisplayName, definition.ClassDisplayName);
    }

    private static TestMethodIdentifierProperty Create(
        string @namespace, string typeName, string scenarioDisplayName, string? classDisplayName)
    {
        ArgumentNullException.ThrowIfNull(scenarioDisplayName);
        var type = string.IsNullOrEmpty(classDisplayName) ? typeName : classDisplayName!;

        // Positional ctor args (assembly, namespace, type, method, method-arity, parameter-types,
        // return-type) — matching xunit.v3's MTP bridge. Scenarios are non-generic, parameterless
        // (the DSL drives them), so arity 0 / no parameters / void.
        return new TestMethodIdentifierProperty(
            AssemblyFullName,
            @namespace,
            type,
            scenarioDisplayName,
            0,
            [],
            VoidReturnTypeName);
    }
}
