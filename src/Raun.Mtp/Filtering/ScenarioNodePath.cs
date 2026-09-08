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
/// Each segment is minimally escaped, not URL-encoded: a display name containing <c>/</c> would
/// otherwise split into extra segments — the platform rejects a slash inside a filter segment
/// outright — so only <c>/</c> (and the <c>%</c> that would make its escape ambiguous) is escaped.
/// Everything else, spaces included, is left literal, because <c>--list-tests</c> shows the
/// platform's own unencoded display name and a user is expected to copy it straight into a filter.
/// The step segment is the step's own name without the numbering prefix discovery adds, because the
/// number is positional: a filter keyed on it would silently select a different step once one is
/// inserted above it.
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

        ScenarioTestIdentity.Resolve(definition, out var @namespace, out var typeName);
        var type = string.IsNullOrEmpty(definition.ClassDisplayName) ? typeName : definition.ClassDisplayName!;

        return string.Concat(
            "/", EscapeSegment(AssemblyName),
            "/", EscapeSegment(@namespace),
            "/", EscapeSegment(type),
            "/", EscapeSegment(definition.DisplayName),
            "/", EscapeSegment(step.DisplayNameTemplate));
    }

    /// <summary>
    /// Escapes a path segment just enough to keep it a single segment — <em>not</em>
    /// <see cref="Uri.EscapeDataString(string)"/>. The only structural requirement is that a segment
    /// cannot itself contain <c>/</c>, since that would split it into extra path segments (and the
    /// platform rejects a literal slash inside a filter segment outright). Full URL-encoding is
    /// stricter than that: it also escapes spaces, and Raun display names (<c>"bulk user import"</c>,
    /// <c>"Given user alice exists"</c>) are natural-language and almost always contain them. A user
    /// who copies a name straight out of <c>--list-tests</c> — which shows the platform's own
    /// unencoded display name — would then have to percent-encode it by hand before a
    /// <c>--treenode-filter</c> would match, which silently returns zero matches otherwise. So this
    /// escapes only <c>%</c> (first, as <c>%25</c>, so the escaping stays unambiguous and reversible)
    /// and then <c>/</c> (as <c>%2F</c>), and leaves every other character — spaces included —
    /// literal. Resist "fixing" this back to <see cref="Uri.EscapeDataString(string)"/>: that was
    /// tried and is precisely what broke the documented filter-from-list-tests workflow.
    /// </summary>
    private static string EscapeSegment(string segment) =>
        segment.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal);

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
