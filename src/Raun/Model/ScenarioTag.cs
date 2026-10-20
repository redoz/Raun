namespace Raun.Model;

/// <summary>One <c>key=value</c> tag on a scenario, from a <see cref="TagAttribute"/>.</summary>
/// <param name="Key">The tag's key, e.g. <c>area</c>.</param>
/// <param name="Value">The tag's value, e.g. <c>auth</c>.</param>
public readonly record struct ScenarioTag(string Key, string Value);
