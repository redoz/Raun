namespace Raun.Model;

/// <summary>
/// Read access to the outputs of already-completed steps, handed to a node's invoke delegate.
/// Generated code resolves a step's arguments with <see cref="Get{T}"/>, passing the index of the
/// producing node.
/// </summary>
public interface IStepInputs
{
    /// <summary>Gets the output produced by the step at <paramref name="producerIndex"/>. Throws
    /// <see cref="InvalidOperationException"/> if that step has not passed: a reader must name its
    /// producer in <see cref="ScenarioNode.DependsOn"/>, and only a generator bug can arrange
    /// otherwise.</summary>
    T Get<T>(int producerIndex);
}
