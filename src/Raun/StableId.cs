using System.Globalization;
using System.Text;

namespace Raun;

/// <summary>
/// Deterministic id generation for scenarios and steps. Ids are an FNV-1a 64-bit hash rendered as
/// 16 lowercase hex digits. They are derived from the scenario's fully-qualified method name and a
/// per-step key (operation name + ordinal) — never from source line numbers, so editing code above
/// a step does not change its identity.
///
/// The generator computes ids at compile time with the same algorithm (see the generator's
/// matching helper); this type is the runtime/reference implementation.
/// </summary>
internal static class StableId
{
    /// <summary>Stable id for a scenario, from its fully-qualified method name.</summary>
    public static string ForScenario(string methodFullName) => Hash(methodFullName);

    /// <summary>Stable id for a step, from its scenario id and a per-step key.</summary>
    public static string ForStep(string scenarioId, string stepKey) => Hash(scenarioId + "/" + stepKey);

    /// <summary>FNV-1a 64-bit over UTF-8, as 16 lowercase hex digits.</summary>
    public static string Hash(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;

        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }
}
