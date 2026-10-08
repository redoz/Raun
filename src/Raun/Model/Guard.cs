namespace Raun.Model;

/// <summary>
/// A branch a node is gated on: the node runs only when the condition node at
/// <see cref="ConditionIndex"/> passed AND selected <see cref="Arm"/> (its
/// <see cref="ScenarioNode.SelectArm"/>; for an <c>if</c>, arm 0 is the body and arm 1 the
/// <c>else</c>; for a <c>switch</c>, arm k is section k). <see cref="Arm"/> may also be
/// <see cref="NoArm"/>: the node runs when a <c>switch</c> without a <c>default</c> matched no
/// section — how a local the sections reassign keeps its earlier value. Nested branches stack
/// guards; all of them must hold.
/// </summary>
public readonly record struct Guard(int ConditionIndex, int Arm)
{
    /// <summary>The arm <see cref="ScenarioNode.SelectArm"/> returns when no arm matches.</summary>
    public const int NoArm = -1;
}
