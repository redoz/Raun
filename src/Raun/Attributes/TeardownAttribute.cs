namespace Raun;

/// <summary>
/// Sets when a scenario's <see cref="Cleanup.Optional"/> teardowns run. Absent, the policy is
/// <see cref="Run.Always"/>. <see cref="Cleanup.Required"/> registrations ignore this entirely —
/// they exist for things whose absence is a leak rather than a choice.
/// </summary>
/// <remarks>
/// Lives beside <see cref="ScenarioAttribute"/> and is read by the generator by metadata name
/// (<c>TeardownAttribute</c>), the same way the scenario attribute is.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class TeardownAttribute(Run run = Run.Always) : Attribute
{
    /// <summary>When the scenario's optional teardowns run.</summary>
    public Run Run { get; } = run;
}
