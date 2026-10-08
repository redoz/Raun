namespace Raun.Model;

/// <summary>
/// A branch a node is gated on: the node runs only when the condition node at
/// <see cref="ConditionIndex"/> passed AND selected <see cref="Arm"/> (its
/// <see cref="ScenarioNode.SelectArm"/>; for an <c>if</c>, arm 0 is the body and arm 1 the
/// <c>else</c>). Nested branches stack guards; all of them must hold.
/// </summary>
public readonly record struct Guard(int ConditionIndex, int Arm);
