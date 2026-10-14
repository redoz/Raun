namespace Raun.Model;

/// <summary>
/// Read access to completed producer outputs and this execution's captured arguments.
/// Preparation resolves producer outputs with <see cref="Get{T}"/>; invocation and metadata
/// share bound values through <see cref="GetArgument{T}"/>.
/// </summary>
public interface IStepInputs
{
    /// <summary>Gets the output produced by the step at <paramref name="producerIndex"/>. Throws
    /// <see cref="InvalidOperationException"/> if that step has not passed: a reader must name its
    /// producer in <see cref="ScenarioNode.DependsOn"/>, and only a generator bug can arrange
    /// otherwise.</summary>
    T Get<T>(int producerIndex);

    /// <summary>Gets a supplied argument captured for the current node execution. Throws when
    /// the node has not launched, unless a formatter explicitly enables a safe constant/producer fallback.
    /// Otherwise the optional delegate supplies type inference only and is never evaluated.</summary>
    T GetArgument<T>(int argumentIndex, Func<T>? unavailable = null, bool allowUnavailable = false)
        => allowUnavailable && unavailable is not null ? unavailable() : throw new InvalidOperationException($"Step argument {argumentIndex} has not been evaluated.");
}
