namespace Raun;

/// <summary>
/// Sets the reported display name for a Given/When/Then DSL operation. The template may contain
/// <c>{parameterName}</c> placeholders that bind to the operation's parameters; the generator
/// substitutes compile-time constants and emits a runtime formatter for the rest. Multiple titles
/// are allowed: the title using the most distinct explicitly supplied parameters wins, with
/// declaration order breaking ties. Titles requiring omitted parameters are never selected.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class StepNameAttribute : Attribute
{
    public StepNameAttribute(string template) => Template = template;

    /// <summary>Display-name template, e.g. <c>"patient {name} exists"</c>.</summary>
    public string Template { get; }

    /// <summary>Per-operation timeout in milliseconds. Zero means inherit the scenario timeout.</summary>
    public int TimeoutMs { get; init; }
}
