using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raun.Generator.Lowering;

internal enum BindingKind { Single, Tuple }

/// <summary>
/// The locals a scenario statement binds: a single local (<c>var x = ...</c>) or a tuple
/// deconstruction (<c>var (a, b) = ...</c> / <c>(var a, var b) = ...</c>). Held as symbols, so two
/// locals that share a name in sibling scopes stay two locals.
/// </summary>
internal sealed record Binding(BindingKind Kind, IReadOnlyList<ILocalSymbol> Locals)
{
    public static Binding Single(ILocalSymbol local) => new(BindingKind.Single, [local]);

    /// <summary>The binding for the left-hand side of an awaited assignment, or null if unsupported.</summary>
    public static Binding? FromAssignment(ExpressionSyntax left, SemanticModel model)
    {
        // appointment = await When.X(...)  — re-assignment of an existing step-output local. This is
        // how an `if` arm redefines a local; the parser's definition map turns the two definitions
        // into a phi.
        if (left is IdentifierNameSyntax identifier)
        {
            return model.GetSymbolInfo(identifier).Symbol is ILocalSymbol local ? Single(local) : null;
        }

        // var (a, b) = ...
        if (left is DeclarationExpressionSyntax { Designation: ParenthesizedVariableDesignationSyntax paren })
        {
            var locals = new List<ILocalSymbol>();
            foreach (var designation in paren.Variables)
            {
                if (designation is not SingleVariableDesignationSyntax single
                    || model.GetDeclaredSymbol(single) is not ILocalSymbol local)
                {
                    return null;
                }

                locals.Add(local);
            }

            return new Binding(BindingKind.Tuple, locals);
        }

        // (var a, var b) = ...
        if (left is TupleExpressionSyntax tuple)
        {
            var locals = new List<ILocalSymbol>();
            foreach (var argument in tuple.Arguments)
            {
                if (argument.Expression is not DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax single }
                    || model.GetDeclaredSymbol(single) is not ILocalSymbol local)
                {
                    return null;
                }

                locals.Add(local);
            }

            return new Binding(BindingKind.Tuple, locals);
        }

        return null;
    }
}
