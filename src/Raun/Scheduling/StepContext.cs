using Raun.Model;

namespace Raun.Scheduling;

/// <summary>
/// The runtime context of a step: the static <see cref="ScenarioNode"/> paired with its
/// runtime-resolved <see cref="DisplayName"/>. The node alone cannot supply the name because it only
/// carries a <see cref="ScenarioNode.DisplayNameTemplate"/> plus an optional
/// <see cref="ScenarioNode.FormatTitleMessage"/> formatter; the scheduler resolves the name once
/// (binding any placeholders to prior steps' outputs) and surfaces the result here so every observer
/// sees the same canonical value the terminal <see cref="StepResult"/> records.
/// </summary>
public sealed class StepContext
{
    /// <summary>The node this context describes.</summary>
    public required ScenarioNode Node { get; init; }

    /// <summary>The formatted display name (runtime placeholders resolved).</summary>
    public required string DisplayName { get; init; }
}
