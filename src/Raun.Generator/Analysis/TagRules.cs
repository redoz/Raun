using System;

namespace Raun.Generator.Analysis;

/// <summary>
/// What makes a <c>[Tag(key, value)]</c> unusable (RAUN037). A tag has to survive being written
/// inside a <c>--treenode-filter</c> property expression (<c>/**[key=value]</c>) and as
/// <c>key:value</c> in the HTML report's URL, so filter syntax is refused outright rather than
/// escaped: the platform's filter has no escape for it.
/// </summary>
internal static class TagRules
{
    /// <summary>Characters <c>--treenode-filter</c> reads as syntax inside or around a property expression.</summary>
    private const string FilterSyntax = "=[]()&|!/*";

    /// <summary>Keys the filter already matches as step properties (Raun.Mtp's ScenarioNodePath).</summary>
    private static readonly string[] Reserved = ["Phase", "Scenario"];

    /// <summary>Why the tag is invalid, or null when it is fine.</summary>
    public static string? Problem(string? key, string? value)
        => Part("key", key) ?? Part("value", value) ?? KeyOnly(key!);

    private static string? Part(string name, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return $"the {name} is empty";
        }

        if (char.IsWhiteSpace(text![0]) || char.IsWhiteSpace(text[text.Length - 1]))
        {
            return $"the {name} has leading or trailing whitespace";
        }

        var at = text.IndexOfAny(FilterSyntax.ToCharArray());
        return at < 0
            ? null
            : $"the {name} contains '{text[at]}'; keys and values cannot contain any of {FilterSyntax} because --treenode-filter reads them as syntax";
    }

    private static string? KeyOnly(string key)
    {
        if (key.IndexOf(':') >= 0)
        {
            return "the key contains ':'; the HTML report writes a tag as key:value";
        }

        foreach (var reserved in Reserved)
        {
            if (string.Equals(key, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return $"the key '{reserved}' is reserved: --treenode-filter already matches it as a step property";
            }
        }

        return null;
    }
}
