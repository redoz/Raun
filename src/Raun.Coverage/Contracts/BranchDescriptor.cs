using Microsoft.CodeAnalysis;

namespace Raun.Coverage;

/// <summary>The syntactic form of a branch; classifiers cannot change this identity.</summary>
public enum BranchForm
{
    If, Switch, Catch, ExceptionFilter, Conditional, ConditionalAccess, Coalesce, ShortCircuit, SwitchExpression, Helper,
}

/// <summary>Stable source-level ownership, including overload signatures.</summary>
public sealed record MemberIdentity(string Assembly, string Type, string Signature)
{
    public string Namespace { get; init; } = string.Empty;
}

/// <summary>Immutable source branch identity supplied to project classification middleware.</summary>
public sealed record BranchDescriptor(string Id, BranchForm Form, FileLinePositionSpan Location, MemberIdentity Member);

/// <summary>Source context available to classification; contains no execution evidence.</summary>
public sealed record BranchClassificationContext(BranchDescriptor Descriptor, SyntaxNode SyntaxNode, SemanticModel SemanticModel);
