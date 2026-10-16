using System.ComponentModel;

namespace Raun;

/// <summary>
/// Base class for a class of steps. A step is a public instance method on a class deriving from
/// <see cref="Given"/>, <see cref="When"/>, <see cref="Then"/>, or a custom
/// phase (<see cref="Phase"/> plus <see cref="PhaseNameAttribute"/>). Inside a step,
/// <see cref="Context"/> provides the running step's services.
/// </summary>
/// <remarks>
/// Raun creates at most one instance of each step class per scenario, on first use, and binds it to
/// that scenario's world. Siblings of a parallel group share the instance, so a step class holds no
/// mutable state of its own.
/// </remarks>
public abstract class Phase
{
    private IScenarioScope? _scope;

    /// <summary>
    /// The context of the step that is running: its cancellation, logging, teardown registrations and
    /// resource effects. Each sibling of a parallel group sees its own. Never null inside a step.
    /// </summary>
    /// <exception cref="InvalidOperationException">No step is running on this flow.</exception>
#pragma warning disable CA1822 // An instance member on purpose: a step reads this.Context next to this.World.
    protected ScenarioContext Context => ScenarioContext.Current
        ?? throw new InvalidOperationException(
            "Context is only available while a step runs; this code ran outside one.");
#pragma warning restore CA1822

    /// <summary>
    /// The scenario's instance of a group of steps, for a group property:
    /// <c>public PatientSteps Patients => Steps&lt;PatientSteps&gt;();</c>. The generator only accepts
    /// group properties written this way.
    /// </summary>
    protected T Steps<T>()
        where T : Phase
        => Scope.Steps<T>();

    private protected IScenarioScope Scope => _scope
        ?? throw new InvalidOperationException(
            $"{GetType().Name} was not created by Raun. Step classes are created per scenario; reach them through a phase property such as Given.");

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal virtual void Bind(IScenarioScope scope) => _scope = scope;
}

/// <summary>Base class for steps that share a scenario world.</summary>
/// <typeparam name="TWorld">The scenario state the steps share.</typeparam>
public abstract class Phase<TWorld> : Phase
    where TWorld : class, IScenarioWorld<TWorld>
{
    /// <summary>The scenario's world.</summary>
    /// <exception cref="InvalidOperationException">The instance was not created by Raun for a scenario.</exception>
    protected TWorld World => ((ScenarioScope<TWorld>)Scope).World;

    internal override void Bind(IScenarioScope scope)
    {
        if (scope is not ScenarioScope<TWorld>)
            throw new InvalidOperationException($"{GetType().Name} requires a scenario world of type {typeof(TWorld).Name}.");
        base.Bind(scope);
    }
}

/// <summary>Steps that arrange a scenario's preconditions.</summary>
[PhaseName("Given")]
#pragma warning disable CA1716 // "Given" reads as the phase it is; the VB keyword clash does not matter here.
public abstract class Given : Phase;
#pragma warning restore CA1716

/// <summary>Steps that perform the action under test.</summary>
[PhaseName("When")]
public abstract class When : Phase;

/// <summary>Steps that assert a scenario's outcome.</summary>
[PhaseName("Then")]
public abstract class Then : Phase;

/// <summary>Steps that arrange a scenario's preconditions.</summary>
[PhaseName("Given")]
#pragma warning disable CA1716 // "Given" reads as the phase it is; the VB keyword clash does not matter here.
public abstract class Given<TWorld> : Phase<TWorld>
#pragma warning restore CA1716
    where TWorld : class, IScenarioWorld<TWorld>;

/// <summary>Steps that perform the action under test.</summary>
[PhaseName("When")]
public abstract class When<TWorld> : Phase<TWorld>
    where TWorld : class, IScenarioWorld<TWorld>;

/// <summary>Steps that assert a scenario's outcome.</summary>
[PhaseName("Then")]
public abstract class Then<TWorld> : Phase<TWorld>
    where TWorld : class, IScenarioWorld<TWorld>;

/// <summary>
/// Names the phase a class of steps belongs to. The generator reads the nearest one in the step
/// class's base chain, so built-in and custom phases are recognised the same way.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class PhaseNameAttribute : Attribute
{
    public PhaseNameAttribute(string name) => Name = name;

    /// <summary>The phase label, e.g. <c>Given</c> or <c>Eventually</c>.</summary>
    public string Name { get; }
}
