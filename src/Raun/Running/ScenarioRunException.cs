using Raun.Model;

namespace Raun.Running;

/// <summary>
/// A scenario failed outside its steps: something threw that no step result could carry, such as a
/// scoped service whose <c>Dispose</c> throws when the scenario's scope is released, a resource token
/// that is declared wrongly, or a definition that does not validate. The run goes on with the other
/// scenarios and throws these at the end.
/// </summary>
public sealed class ScenarioRunException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ScenarioRunException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ScenarioRunException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ScenarioRunException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal ScenarioRunException(ScenarioDefinition definition, Exception cause)
        : base($"Scenario '{definition.DisplayName}' failed outside its steps: {cause.GetType().Name}: {cause.Message}", cause)
    {
        ScenarioId = definition.ScenarioId;
    }

    /// <summary>The scenario that failed, when known.</summary>
    public string? ScenarioId { get; }
}
