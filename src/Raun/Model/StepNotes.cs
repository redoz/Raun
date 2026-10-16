using System.Globalization;

namespace Raun.Model;

/// <summary>Formats step metadata without letting unavailable inputs change the outcome.</summary>
internal static class StepNotes
{
    internal static StepMessage FormatTitle(ScenarioNode node, MessageFormattingContext formatting, IStepInputs? inputs = null)
    {
        try
        {
            if (node.FormatTitleMessage is { } rich)
            {
                var titleFormatting = formatting.ForStep(node.Index).WithFormatProvider(CultureInfo.InvariantCulture);
                return rich(inputs ?? UnavailableInputs.Instance, titleFormatting);
            }
            if (node.FormatDisplayName is { } legacy)
            {
                return StepMessage.FromText(legacy(inputs ?? UnavailableInputs.Instance));
            }
        }
        catch
        {
            // Missing inputs or a failing title must not change the test outcome.
        }
        return StepMessage.FromText(node.DisplayNameTemplate);
    }

    internal static StepMessage FormatCompletedTitle(
        ScenarioNode node, StepMessage initialTitle, object? output, MessageFormattingContext formatting)
    {
        if (output is not null && node.FormatCompletedTitleMessage is { } completed)
        {
            try
            {
                var titleFormatting = formatting.ForStep(node.Index).WithFormatProvider(CultureInfo.InvariantCulture);
                return completed(initialTitle, output, titleFormatting) ?? initialTitle;
            }
            catch
            {
                // Completion formatting is evidence only; preserve the title recorded at start.
            }
        }
        return initialTitle;
    }

    internal static StepMessage[] FormatMessages(ScenarioNode node, MessageFormattingContext formatting, IStepInputs? inputs = null)
    {
        var notes = new List<StepMessage>();
        var available = inputs ?? UnavailableInputs.Instance;
        if (node.FormatNoteMessages.Count > 0)
        {
            foreach (var formatter in node.FormatNoteMessages)
            {
                Add(() => formatter(available, formatting.ForStep(node.Index)));
            }
        }
        else
        {
            foreach (var formatter in node.FormatNotes)
            {
                Add(() => StepMessage.FromText(formatter(available)));
            }
        }
        return [.. notes];

        void Add(Func<StepMessage> format)
        {
            try
            {
                var message = format();
                if (!string.IsNullOrWhiteSpace(message.Text)) { notes.Add(message); }
            }
            catch
            {
                // Each explanation is independent, so one unavailable input cannot erase the others.
            }
        }
    }

    private sealed class UnavailableInputs : IStepInputs
    {
        public static UnavailableInputs Instance { get; } = new();

        public T Get<T>(int producerIndex)
            => throw new InvalidOperationException($"Step input {producerIndex} is unavailable because the scenario did not run.");
    }
}
