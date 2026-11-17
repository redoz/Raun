namespace Raun.Model;

/// <summary>
/// A step input was read where it has no value: its producer did not pass, or the scenario never
/// ran. A title or note formatted for a step that was skipped hits this as a matter of course, which
/// is why it is told apart from a formatter that fails for a reason of its own.
/// </summary>
internal sealed class StepInputUnavailableException : InvalidOperationException
{
    public StepInputUnavailableException()
    {
    }

    public StepInputUnavailableException(string message)
        : base(message)
    {
    }

    public StepInputUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
