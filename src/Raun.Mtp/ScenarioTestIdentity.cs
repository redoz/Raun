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
/// <see cref="ScenarioDefinition.MethodName"/> is emitted by the generator as
/// <c>{declaringTypeFqn}.{methodSimpleName}</c>, where <c>declaringTypeFqn</c> is
/// <c>{namespace}.{typeSimpleName}</c> (an empty namespace for a top-level type). The split is the
/// inverse: the last dotted segment is the method, the segment before it the simple type name, and
/// anything ahead of that the namespace. Nested types (rare for scenario hosts) fold the outer type
/// name into the namespace segment — still a stable, non-empty grouping, just not a perfect class
/// split; the generator would have to emit the parts separately to do better.
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
    /// definition (see <see cref="Resolve"/>), but the method node is the human scenario display
    /// name so a runner groups steps under the scenario name. A non-empty
    /// <see cref="ScenarioDefinition.ClassDisplayName"/> overrides the type name.
    /// </summary>
    public static TestMethodIdentifierProperty Create(ScenarioDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Resolve(definition, out var @namespace, out var typeName);
        return Create(@namespace, typeName, definition.DisplayName, definition.ClassDisplayName);
    }

    /// <summary>
    /// The namespace and declaring type of a scenario: what the generator recorded on the
    /// definition when it is there, otherwise the dotted split of <see cref="ScenarioDefinition.MethodName"/>
    /// (output of a generator older than the identity members). <see cref="ScenarioNodePath"/> uses
    /// the same derivation so filtering and IDE grouping agree.
    /// </summary>
    public static void Resolve(ScenarioDefinition definition, out string @namespace, out string typeName)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.TypeName.Length > 0)
        {
            @namespace = definition.Namespace;
            typeName = definition.TypeName;
            return;
        }

        Split(definition.MethodName, out @namespace, out typeName, out _);
    }

    /// <summary>
    /// Builds the method-identity property from a method FQN alone: namespace and type are derived
    /// by <see cref="Split"/>. This is the fallback path; prefer <see cref="Create(ScenarioDefinition)"/>.
    /// </summary>
    public static TestMethodIdentifierProperty Create(
        string methodFullName, string scenarioDisplayName, string? classDisplayName = null)
    {
        Split(methodFullName, out var @namespace, out var typeName, out _);
        return Create(@namespace, typeName, scenarioDisplayName, classDisplayName);
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

    /// <summary>
    /// Splits a <c>{namespace}.{type}.{method}</c> name into its parts. The namespace (and, for a
    /// bare name, the type) come back empty rather than throwing.
    /// </summary>
    internal static void Split(string methodFullName, out string @namespace, out string typeName, out string methodName)
    {
        ArgumentNullException.ThrowIfNull(methodFullName);

        var lastDot = methodFullName.LastIndexOf('.');
        if (lastDot < 0)
        {
            // No declaring type in the name; treat the whole thing as the method.
            @namespace = string.Empty;
            typeName = string.Empty;
            methodName = methodFullName;
            return;
        }

        methodName = methodFullName.Substring(lastDot + 1);
        var declaringType = methodFullName.Substring(0, lastDot);

        var typeDot = declaringType.LastIndexOf('.');
        if (typeDot < 0)
        {
            // Top-level type: no namespace.
            @namespace = string.Empty;
            typeName = declaringType;
        }
        else
        {
            @namespace = declaringType.Substring(0, typeDot);
            typeName = declaringType.Substring(typeDot + 1);
        }
    }
}
