using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Raun.Generator.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Lowering;

/// <summary>A step output an argument reads: the local, and the identifier that reads it.</summary>
internal readonly record struct StepOutputRead(ILocalSymbol Local, IdentifierNameSyntax Node);

/// <summary>
/// Why part of an argument cannot be lowered: the offending node, what it is (as written), and the
/// reason. The analyzer reports it as RAUN007; the parser refuses the scenario with it (RAUN017).
/// </summary>
internal readonly record struct ArgumentViolation(SyntaxNode Node, string Subject, string Reason)
{
    /// <summary>RAUN007's message format, and the sentence RAUN017 carries: one wording for both.</summary>
    public const string MessageFormat = "Step argument cannot use '{0}': {1}";

    /// <summary>The violation as <see cref="MessageFormat"/> spells it.</summary>
    public string Describe()
        => string.Format(System.Globalization.CultureInfo.InvariantCulture, MessageFormat, Subject, Reason);
}

/// <summary>An argument re-hosted into the generated step body, with everything the lowering saw.</summary>
internal sealed record LoweredArgument<TNode>(
    TNode Node,
    IReadOnlyList<StepOutputRead> Reads,
    IReadOnlyList<ArgumentViolation> Violations)
    where TNode : SyntaxNode;

/// <summary>A DSL call's value and type arguments, lowered.</summary>
internal sealed record LoweredCall(
    IReadOnlyList<LoweredArgument<ArgumentSyntax>> Arguments,
    LoweredArgument<TypeArgumentListSyntax>? TypeArguments)
{
    public IEnumerable<ArgumentViolation> Violations
        => Arguments.SelectMany(a => a.Violations).Concat(TypeArguments?.Violations ?? []);

    public IEnumerable<StepOutputRead> Reads => Arguments.SelectMany(a => a.Reads);
}

/// <summary>
/// The one definition of what a step argument may contain, and how it is re-hosted.
/// </summary>
/// <remarks>
/// <para>
/// An argument is an ordinary C# expression, evaluated inside the consuming step when that step
/// starts. It moves from the scenario method into a static lambda in <c>Raun.Generated</c>, so every
/// name in it must mean there what it meant in the scenario:
/// </para>
/// <list type="bullet">
/// <item>a step output becomes the producing step's recorded result — a read, never a re-run;</item>
/// <item>a type, namespace, or static member named by a simple name is emitted fully qualified, so
/// the scenario's own class and namespace need not be in scope;</item>
/// <item><c>nameof(x)</c> becomes the string it always was.</item>
/// </list>
/// <para>
/// What cannot move, or would hide scenario structure, is a violation: a scenario local no step
/// produced, a scenario parameter, an instance member, a private member, an <c>await</c>, a step
/// call, or a write to a step output.
/// </para>
/// <para>
/// The analyzer and the parser both run this same pass — the analyzer for its violations, the parser
/// for the lowered node and its reads — so, given the same step outputs, they cannot disagree about
/// an argument. Binding is by
/// symbol throughout: a lambda parameter that shadows a step local is not a read of it, and an
/// object-initializer member that shares a local's name is not rewritten.
/// </para>
/// </remarks>
internal sealed class ArgumentLowering : CSharpSyntaxRewriter
{
    private const string NotAStepOutput =
        "it is a local no step produced; only a step's result can flow into a later step, so produce the value with a step or compute it inside one";

    private const string ScenarioParameter =
        "it is a parameter of the scenario method, which is never called: its body is lowered into a graph, not executed";

    private const string LocalFunction =
        "it is a local function of the scenario body, which is lowered, not executed; make it a static method";

    private const string InstanceMember =
        "it is an instance member, and the step runs in static generated code; make it static";

    private const string Unreachable =
        "it is not visible outside its type, and the step runs in generated code; make it internal";

    private const string Await =
        "an argument is evaluated when its step starts and cannot await; do the asynchronous work inside a step";

    private const string NestedStep =
        "a step cannot run inside another step's argument; await it as its own statement and pass its result";

    private const string WritesStepOutput =
        "an argument must not change a step's result, which later steps read too";

    private readonly SemanticModel _model;
    private readonly SyntaxNode _root;
    private readonly Func<ISymbol, ExpressionSyntax?> _stepValue;
    private readonly List<StepOutputRead> _reads = [];
    private readonly List<ArgumentViolation> _violations = [];

    private ArgumentLowering(SemanticModel model, SyntaxNode root, Func<ISymbol, ExpressionSyntax?> stepValue)
    {
        _model = model;
        _root = root;
        _stepValue = stepValue;
    }

    /// <summary>
    /// Lowers every argument of a DSL call, and its explicit type arguments — the parser and the
    /// analyzer both enter here, so they walk exactly the same nodes.
    /// </summary>
    public static LoweredCall LowerCall(
        SemanticModel model, InvocationExpressionSyntax call, Func<ISymbol, ExpressionSyntax?> stepValue)
        => new(
            call.ArgumentList.Arguments.Select(argument => Lower(model, argument, stepValue)).ToList(),
            call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic }
                ? Lower(model, generic.TypeArgumentList, stepValue)
                : null);

    /// <summary>
    /// Lowers <paramref name="node"/>, which must be part of <paramref name="model"/>'s tree.
    /// <paramref name="stepValue"/> spells a scenario-level symbol that carries a step's value — a
    /// step-output local, or a LINQ unroll's loop variable — and returns null for anything else.
    /// </summary>
    public static LoweredArgument<TNode> Lower<TNode>(
        SemanticModel model, TNode node, Func<ISymbol, ExpressionSyntax?> stepValue)
        where TNode : SyntaxNode
    {
        var lowering = new ArgumentLowering(model, node, stepValue);
        var lowered = (TNode)lowering.Visit(node);
        return new LoweredArgument<TNode>(lowered, lowering._reads, lowering._violations);
    }

    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node) => Rehost(node, node);

    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        => Rehost(node, (GenericNameSyntax)base.VisitGenericName(node)!);

    /// <summary>
    /// Re-hosts one simple name. <paramref name="name"/> is the node with its own type arguments
    /// already lowered (the node itself, for an identifier).
    /// </summary>
    private SyntaxNode Rehost(SimpleNameSyntax node, SimpleNameSyntax name)
    {
        if (node.IsVar || _model.GetSymbolInfo(node).Symbol is not { } symbol || DeclaredWithin(symbol))
        {
            return name;
        }

        // Bound by its context (after a `.`, an initializer's member, a label): nothing to rewrite, but
        // a private member reached through a qualifier is as unreachable as one named directly.
        if (IsBoundByContext(node))
        {
            return IsTypeOrMember(symbol) && !IsReachable(symbol) ? Violate(node, Unreachable) : name;
        }

        if (_stepValue(symbol) is { } value)
        {
            if (symbol is ILocalSymbol local && node is IdentifierNameSyntax identifier)
            {
                _reads.Add(new StepOutputRead(local, identifier));
            }

            return value.WithTriviaFrom(node);
        }

        return symbol switch
        {
            ILocalSymbol => Violate(node, NotAStepOutput),
            IParameterSymbol => Violate(node, ScenarioParameter),
            IMethodSymbol { MethodKind: MethodKind.LocalFunction } => Violate(node, LocalFunction),
            INamespaceSymbol ns => TypeSyntaxFactory.GlobalNamespaceName(ns).WithTriviaFrom(node),
            ITypeSymbol type => IsReachable(type)
                ? TypeSyntaxFactory.From(type).WithTriviaFrom(node)
                : Violate(node, Unreachable),
            _ when IsTypeOrMember(symbol) => QualifyMember(node, symbol, name),
            _ => name,
        };
    }

    private static bool IsTypeOrMember(ISymbol symbol)
        => symbol is ITypeSymbol or IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol;

    public override SyntaxNode? VisitThisExpression(ThisExpressionSyntax node) => Violate(node, InstanceMember);

    public override SyntaxNode? VisitBaseExpression(BaseExpressionSyntax node) => Violate(node, InstanceMember);

    /// <summary>An <c>await</c> evaluated with the argument is refused; one inside a lambda or local
    /// function the argument declares runs whenever that callback runs, and is the callback's business.</summary>
    public override SyntaxNode? VisitAwaitExpression(AwaitExpressionSyntax node)
        => node.Ancestors().TakeWhile(ancestor => ancestor != _root)
            .Any(ancestor => ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
                ? base.VisitAwaitExpression(node)
                : Violate(node, "await", Await);

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // nameof reads nothing at run time; its value is the name as written.
        if (_model.GetOperation(node) is INameOfOperation { ConstantValue: { HasValue: true, Value: string name } })
        {
            return LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(name)).WithTriviaFrom(node);
        }

        if (node.Expression is MemberAccessExpressionSyntax member
            && SymbolHelpers.PhaseOf(member.Expression, _model) is not null)
        {
            return Violate(node, member.ToString(), NestedStep);
        }

        return base.VisitInvocationExpression(node);
    }

    public override SyntaxNode? VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (IsStepOutput(node.Left))
        {
            Violate(node.Left, WritesStepOutput);
        }

        return base.VisitAssignmentExpression(node);
    }

    public override SyntaxNode? VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.Kind() is SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression
            && IsStepOutput(node.Operand))
        {
            Violate(node.Operand, WritesStepOutput);
        }

        return base.VisitPrefixUnaryExpression(node);
    }

    public override SyntaxNode? VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.Kind() is SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression
            && IsStepOutput(node.Operand))
        {
            Violate(node.Operand, WritesStepOutput);
        }

        return base.VisitPostfixUnaryExpression(node);
    }

    public override SyntaxNode? VisitArgument(ArgumentSyntax node)
    {
        if (node.RefKindKeyword.Kind() is SyntaxKind.RefKeyword or SyntaxKind.OutKeyword
            && IsStepOutput(node.Expression))
        {
            Violate(node.Expression, WritesStepOutput);
        }

        return base.VisitArgument(node);
    }

    /// <summary><c>new { customer }</c> takes its member name from the identifier; once the identifier
    /// is rewritten into something else, the name has to be spelled out.</summary>
    public override SyntaxNode? VisitAnonymousObjectMemberDeclarator(AnonymousObjectMemberDeclaratorSyntax node)
    {
        if (node.NameEquals is not null || node.Expression is not IdentifierNameSyntax name)
        {
            return base.VisitAnonymousObjectMemberDeclarator(node);
        }

        var expression = (ExpressionSyntax)Visit(name)!;
        return expression is IdentifierNameSyntax
            ? node.WithExpression(expression)
            : node.WithNameEquals(NameEquals(IdentifierName(name.Identifier.WithoutTrivia())))
                .WithExpression(expression.WithoutLeadingTrivia())
                .WithLeadingTrivia(name.GetLeadingTrivia());
    }

    /// <summary>
    /// A simple name whose meaning comes from the syntax around it rather than from scope: a member
    /// after <c>.</c> or <c>?.</c>, the right side of a qualified name, an argument or tuple label, an
    /// anonymous-object or pattern member name, or the member an object/<c>with</c> initializer
    /// assigns. It moves with its context and needs no rewriting.
    /// </summary>
    private static bool IsBoundByContext(SimpleNameSyntax node)
        => node.Parent switch
        {
            MemberAccessExpressionSyntax access when access.Expression == node => IsPatternMemberPath(access),
            MemberAccessExpressionSyntax access => access.Name == node,
            QualifiedNameSyntax qualified => qualified.Right == node,
            MemberBindingExpressionSyntax or AliasQualifiedNameSyntax => true,
            NameColonSyntax or NameEqualsSyntax or ExpressionColonSyntax => true,
            AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax initializer } assignment
                => assignment.Left == node
                    && initializer.Kind() is SyntaxKind.ObjectInitializerExpression or SyntaxKind.WithInitializerExpression,
            _ => false,
        };

    /// <summary>True when <paramref name="access"/> heads an extended property pattern's member path
    /// (<c>{ Customer.Name: … }</c>): its leftmost name is a member of the pattern's input, not a name
    /// in scope.</summary>
    private static bool IsPatternMemberPath(MemberAccessExpressionSyntax access)
    {
        SyntaxNode top = access;
        while (top.Parent is MemberAccessExpressionSyntax outer && outer.Expression == top)
        {
            top = outer;
        }

        return top.Parent is ExpressionColonSyntax;
    }

    /// <summary>True for a symbol the argument declares itself — a lambda parameter, a pattern or
    /// <c>out</c> variable, a lambda's local, a local function and its type parameters — which lives
    /// and dies inside the lowered expression.</summary>
    private bool DeclaredWithin(ISymbol symbol)
        => symbol is ILocalSymbol or IParameterSymbol or ITypeParameterSymbol or IRangeVariableSymbol
                or IMethodSymbol { MethodKind: MethodKind.LocalFunction }
            && symbol.OriginalDefinition.DeclaringSyntaxReferences.Any(
                r => r.SyntaxTree == _root.SyntaxTree && _root.Span.Contains(r.Span));

    /// <summary>True when <paramref name="target"/> is rooted in a step output — the local itself, or a
    /// member or element reached through it.</summary>
    private bool IsStepOutput(ExpressionSyntax target)
    {
        while (true)
        {
            switch (target)
            {
                case MemberAccessExpressionSyntax access:
                    target = access.Expression;
                    continue;
                case ElementAccessExpressionSyntax element:
                    target = element.Expression;
                    continue;
                case ParenthesizedExpressionSyntax parenthesized:
                    target = parenthesized.Expression;
                    continue;
                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } suppressed:
                    target = suppressed.Operand;
                    continue;
                case IdentifierNameSyntax identifier:
                    return _model.GetSymbolInfo(identifier).Symbol is ILocalSymbol local
                        && !DeclaredWithin(local)
                        && _stepValue(local) is not null;
                default:
                    return false;
            }
        }
    }

    /// <summary>A member named by a simple name: <c>Type.Member</c> for a visible static member; a
    /// violation for an instance member (the scenario's implicit <c>this</c>) or a private one.</summary>
    private SyntaxNode QualifyMember(SimpleNameSyntax node, ISymbol member, SimpleNameSyntax name)
    {
        if (!member.IsStatic)
        {
            return Violate(node, InstanceMember);
        }

        if (!IsReachable(member))
        {
            return Violate(node, Unreachable);
        }

        return MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                TypeSyntaxFactory.From(member.ContainingType),
                name.WithoutTrivia())
            .WithTriviaFrom(node);
    }

    /// <summary>Whether code elsewhere in the assembly — the generated file — can name the symbol:
    /// neither it nor any type containing it is private or protected. Type arguments are not checked
    /// here: every one the scenario spelled is a name of its own, visited and checked on its own.</summary>
    private static bool IsReachable(ISymbol symbol)
    {
        for (var current = symbol; current is not null and not INamespaceSymbol; current = current.ContainingSymbol)
        {
            if (current.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected
                or Accessibility.ProtectedAndInternal)
            {
                return false;
            }
        }

        return true;
    }

    private SyntaxNode Violate(SyntaxNode node, string reason) => Violate(node, node.ToString(), reason);

    private SyntaxNode Violate(SyntaxNode node, string subject, string reason)
    {
        _violations.Add(new ArgumentViolation(node, subject, reason));
        return node;
    }
}
