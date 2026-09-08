using System.Reflection;
using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Model;

namespace Raun.Mtp;

/// <summary>
/// The address a step presents to <c>--treenode-filter</c>:
/// <c>/{assembly}/{namespace}/{class}/{scenario}/{step}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Five segments rather than the conventional four because in Raun a <em>step</em> is a test node:
/// <c>/*/*/*/booking/*</c> selects a scenario and <c>/*/*/*/booking/*reminder*</c> one step inside it.
/// The segments come from the same derivation <see cref="ScenarioTestIdentity"/> uses, so filtering
/// and IDE grouping agree.
/// </para>
/// <para>
/// Every segment is URL-encoded: the platform documents the path as segment-encoded, and a display
/// name containing <c>/</c> would otherwise split into extra segments — the platform rejects a slash
/// inside a filter segment outright. The step segment is the step's own name without the numbering
/// prefix discovery adds, because the number is positional: a filter keyed on it would silently
/// select a different step once one is inserted above it.
/// </para>
/// </remarks>
internal static class ScenarioNodePath
{
    /// <summary>Property key for a step's phase marker (Given/When/Then, or a custom marker).</summary>
    private const string PhaseKey = "Phase";

    /// <summary>Property key for the owning scenario's display name.</summary>
    private const string ScenarioKey = "Scenario";

    private static readonly string AssemblyName =
        Assembly.GetEntryAssembly()?.GetName().Name ?? string.Empty;

    /// <summary>Builds the encoded five-segment path for one step.</summary>
    public static string For(ScenarioDefinition definition, ScenarioNode step)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(step);

        ScenarioTestIdentity.Split(definition.MethodName, out var @namespace, out var typeName, out _);
        var type = string.IsNullOrEmpty(definition.ClassDisplayName) ? typeName : definition.ClassDisplayName!;

        return string.Concat(
            "/", Uri.EscapeDataString(AssemblyName),
            "/", Uri.EscapeDataString(@namespace),
            "/", Uri.EscapeDataString(type),
            "/", Uri.EscapeDataString(definition.DisplayName),
            "/", Uri.EscapeDataString(step.DisplayNameTemplate));
    }

    /// <summary>
    /// The properties a filter's <c>[key=value]</c> expression can match. The platform inspects only
    /// <see cref="TestMetadataProperty"/>, and never reads the bag at all when no segment carries a
    /// property expression.
    /// </summary>
    public static PropertyBag Properties(ScenarioDefinition definition, ScenarioNode step)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(step);

        return new PropertyBag(
            new TestMetadataProperty(PhaseKey, step.Phase),
            new TestMetadataProperty(ScenarioKey, definition.DisplayName));
    }
}
