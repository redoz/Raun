namespace Raun;

/// <summary>Adds an explanation to a scenario step while preserving its task and result.</summary>
public static class TaskNoteExtensions
{
    /// <summary>Adds plain text explaining why this step is performed.</summary>
    public static Task Note(this Task task, string message) => task.Note(StepMessage.FromText(message));

    /// <summary>Adds formatted text explaining why this step is performed.</summary>
    public static Task Note(this Task task, FormattableString message)
        => task.Note(StepMessage.Create(FormattingContext(), message));

    /// <summary>Adds captured interpolation explaining why this step is performed.</summary>
    public static Task Note(this Task task, StepMessageInterpolatedStringHandler message)
        => task.Note(message.Materialize(FormattingContext()));

    /// <summary>Adds a frozen explanation and preserves the task.</summary>
    public static Task Note(this Task task, StepMessage message)
    {
        Record(task, message);
        return task;
    }

    /// <summary>Adds plain text explaining why this step is performed, preserving its result type.</summary>
    public static Task<T> Note<T>(this Task<T> task, string message) => task.Note(StepMessage.FromText(message));

    /// <summary>Adds formatted text explaining why this step is performed, preserving its result type.</summary>
    public static Task<T> Note<T>(this Task<T> task, FormattableString message)
        => task.Note(StepMessage.Create(FormattingContext(), message));

    /// <summary>Adds captured interpolation while preserving the task's result type.</summary>
    public static Task<T> Note<T>(this Task<T> task, StepMessageInterpolatedStringHandler message)
        => task.Note(message.Materialize(FormattingContext()));

    /// <summary>Adds a frozen explanation while preserving the task's result type.</summary>
    public static Task<T> Note<T>(this Task<T> task, StepMessage message)
    {
        Record(task, message);
        return task;
    }

    private static MessageFormattingContext FormattingContext()
        => ScenarioContext.Current?.MessageFormatting ?? new(new ResourceIdentityResolver());

    private static void Record(Task task, StepMessage message)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(message);
        ScenarioContext.Current?.AddNote(message);
    }
}
