using System;

namespace Raun;

/// <summary>
/// Thrown by a resource verb when the claiming step and another step of the same scenario both
/// touch one <see cref="ResourceIdentity"/>, at least one of them mutating it, and no dependency path
/// orders them. The graph would run the two concurrently, so this is a defect in the scenario: give
/// one step a dependency on the other, or declare one access as <c>[Read]</c>. Nothing was locked and
/// nothing waited — the conflict is reported, never serialized.
/// </summary>
#pragma warning disable CA1032 // Standard exception constructors: a conflict without its two steps and identity is meaningless.
public sealed class ResourceConflictException : InvalidOperationException
#pragma warning restore CA1032
{
    /// <summary>Creates the exception for a conflict between the claiming step and an earlier claimant.</summary>
    public ResourceConflictException(
        ResourceIdentity identity,
        string stepDisplayName,
        ResourceEffect verb,
        string otherStepDisplayName,
        ResourceEffect otherVerb)
        : base(
            $"Step '{stepDisplayName}' ({verb}) and step '{otherStepDisplayName}' ({otherVerb}) both touch "
            + $"{identity} and nothing orders them; add a dependency between them or declare one access as [Read].")
    {
        Identity = identity;
        StepDisplayName = stepDisplayName;
        Effect = verb;
        OtherStepDisplayName = otherStepDisplayName;
        OtherEffect = otherVerb;
    }

    /// <summary>The identity both steps touch.</summary>
    public ResourceIdentity Identity { get; }

    /// <summary>The step whose claim was refused (the later of the two to claim).</summary>
    public string StepDisplayName { get; }

    /// <summary>The refused claim's verb.</summary>
    public ResourceEffect Effect { get; }

    /// <summary>The step that already held a claim on the identity.</summary>
    public string OtherStepDisplayName { get; }

    /// <summary>The earlier claim's verb.</summary>
    public ResourceEffect OtherEffect { get; }
}
