using System.Globalization;

namespace Raun.Model;

/// <summary>
/// Formats step metadata without letting a formatter change the outcome. A formatter that reads an
/// input with no value falls back silently; one that throws anything else also falls back, and is
/// handed to <c>onFailure</c> so the step can say so.
/// </summary>
internal static class StepNotes
{
    internal static StepMessage FormatTitle(
        ScenarioNode node, MessageFormattingContext formatting, IStepInputs? inputs = null, Action<Exception>? onFailure = null)
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
        catch (StepInputUnavailableException)
        {
        }
        catch (Exception ex)
        {
            onFailure?.Invoke(ex);
        }
        return StepMessage.FromText(node.DisplayNameTemplate);
    }

    internal static StepMessage FormatCompletedTitle(
        ScenarioNode node, StepMessage initialTitle, object? output, MessageFormattingContext formatting,
        Action<Exception>? onFailure = null)
    {
        if (output is not null && node.FormatCompletedTitleMessage is { } completed)
        {
            try
            {
                var titleFormatting = formatting.ForStep(node.Index).WithFormatProvider(CultureInfo.InvariantCulture);
                return completed(initialTitle, output, titleFormatting) ?? initialTitle;
            }
            catch (Exception ex)
            {
                onFailure?.Invoke(ex);
            }
        }
        return initialTitle;
    }

    internal static StepMessage[] FormatMessages(
        ScenarioNode node, MessageFormattingContext formatting, IStepInputs? inputs = null, Action<Exception>? onFailure = null)
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
            catch (StepInputUnavailableException)
            {
            }
            catch (Exception ex)
            {
                onFailure?.Invoke(ex);
            }
        }
    }

    private sealed class UnavailableInputs : IStepInputs
    {
        public static UnavailableInputs Instance { get; } = new();

        public T Get<T>(int producerIndex)
            => throw new StepInputUnavailableException($"Step input {producerIndex} is unavailable because the scenario did not run.");
    }
}
