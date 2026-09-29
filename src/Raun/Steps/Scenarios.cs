namespace Raun;

/// <summary>
/// Base class for a class of scenarios whose steps share a <typeparamref name="TWorld"/>. Declare
/// the phases once, in a base of your own, as properties:
/// <code>
/// public abstract class AppointmentScenarios : Scenarios&lt;AppointmentWorld&gt;
/// {
///     public AppointmentGiven Given => Steps&lt;AppointmentGiven&gt;();
///     public AppointmentWhen When => Steps&lt;AppointmentWhen&gt;();
///     public AppointmentThen Then => Steps&lt;AppointmentThen&gt;();
/// }
/// </code>
/// and write each <c>[Scenario]</c> as an instance method of a class deriving from it.
/// </summary>
/// <remarks>
/// A scenario body is read by Raun's generator, not run: the generator follows each call's receiver
/// by type (<c>Given.Patients.Exists(…)</c> is a step of <c>PatientSteps</c>) and calls it on the
/// instance Raun created for the scenario. The properties are what the compiler, the IDE and a reader
/// see, which is why they may only return <see cref="Steps{T}"/>.
/// </remarks>
/// <typeparam name="TWorld">The scenario state the steps share.</typeparam>
public abstract class Scenarios<TWorld>
    where TWorld : class, IScenarioWorld<TWorld>
{
    /// <summary>The scenario's instance of a class of steps, for a phase property.</summary>
    /// <exception cref="InvalidOperationException">Always, when called: a scenario body is never run directly.</exception>
#pragma warning disable CA1822 // An instance member on purpose: a static one would make every phase property "could be static" in the suite.
    protected T Steps<T>()
        where T : Phase<TWorld>
        => throw new InvalidOperationException(
            "A scenario body is lowered by Raun's generator and never run directly; run the scenario through Raun.");
#pragma warning restore CA1822
}
