namespace Raun;

/// <summary>
/// Tags a scenario with a <c>key=value</c> pair. Put it on a <c>[Scenario]</c> method, on the class
/// that declares scenarios (every scenario in it, nested classes included, gets the tag), or on the
/// assembly. It repeats, and one key may carry several values: <c>[Tag("area", "auth")]</c>
/// <c>[Tag("area", "billing")]</c>.
/// </summary>
/// <remarks>
/// <para>
/// Put <c>[Tag]</c>s on your own attribute class to name a set of tags: applying that attribute
/// applies every tag on its class, and such classes may nest. The generator reads all of this at
/// compile time by metadata name (<c>Raun.TagAttribute</c>), so an attribute from a referenced
/// library works the same as one declared next to the scenarios.
/// </para>
/// <para>
/// Tags select scenarios with <c>--treenode-filter "/**[area=auth]"</c> and show in the IDE,
/// the TRX and the HTML report. Keys and values are matched case-insensitively; RAUN037 rejects an
/// empty key or value, filter syntax characters, and the keys the filter already uses.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class TagAttribute(string key, string value) : Attribute
{
    /// <summary>The tag's key, e.g. <c>area</c>.</summary>
    public string Key { get; } = key;

    /// <summary>The tag's value, e.g. <c>auth</c>.</summary>
    public string Value { get; } = value;
}
