namespace Raun;

/// <summary>
/// Thrown when scenarios are listed while some were registered by code generated for another
/// contract than this runtime's (see <see cref="ScenarioRegistry.ContractVersion"/>). The message
/// names the assemblies to rebuild.
/// </summary>
public sealed class ScenarioContractException : InvalidOperationException
{
    /// <summary>Creates the exception without a message.</summary>
    public ScenarioContractException()
    {
    }

    /// <summary>Creates the exception with what was refused and what to rebuild.</summary>
    public ScenarioContractException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    public ScenarioContractException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
