using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Generator.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Lowering;

/// <summary>Projects an annotated key to its canonical resource identity without changing the step argument.</summary>
internal static class ResourceKeyBinding
{
    public static ExpressionSyntax Apply(IParameterSymbol parameter, ExpressionSyntax argument)
    {
        if (AttributeReader.ResourceKeyType(parameter) is not { } resourceType)
        {
            return argument;
        }
        ExpressionSyntax keyText = parameter.Type.SpecialType == SpecialType.System_String
            ? ParenthesizedExpression(CastExpression(NullableType(PredefinedType(Token(SyntaxKind.StringKeyword))), ParenthesizedExpression(argument)))
            : MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                ParenthesizedExpression(CastExpression(Names.Global("Raun", "ResourceKey"), ParenthesizedExpression(argument))), IdentifierName("Value"));
        var requiredKey = BinaryExpression(SyntaxKind.CoalesceExpression, keyText,
            ThrowExpression(ObjectCreationExpression(Names.Global("System", "ArgumentNullException"))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(
                    Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(parameter.Name))))))));
        return ObjectCreationExpression(Names.Global("Raun", "ResourceIdentity"))
            .WithArgumentList(ArgumentList(SeparatedList(new[]
            {
                Argument(TypeOfExpression(TypeSyntaxFactory.From(resourceType))),
                Argument(CastExpression(Names.Global("Raun", "ResourceKey"), ParenthesizedExpression(requiredKey))),
            })));
    }
}