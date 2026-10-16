using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Generator.Diagnostics;
using Raun.Generator.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Lowering;

// Transient semantic bindings: only lowered value-equal IR leaves the parse stage.
internal sealed record BoundScenarioCase(string Key, int SourceOrdinal, IReadOnlyDictionary<IParameterSymbol, ExpressionSyntax> Arguments);

internal sealed record ScenarioCaseBindingResult(IReadOnlyList<BoundScenarioCase> Cases, EquatableArray<ScenarioDiagnostic> Diagnostics);

internal static class ScenarioCaseBinding
{
    public static ScenarioCaseBindingResult Bind(SemanticModel model, IMethodSymbol method, MethodDeclarationSyntax syntax)
    {
        var attributes = method.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == "Raun.ScenarioCaseAttribute").ToArray();
        var isScenario = method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "Raun.ScenarioAttribute");
        var diagnostics = new List<ScenarioDiagnostic>();
        var cases = new List<BoundScenarioCase>();
        void Report(DiagnosticDescriptor descriptor, Location location, params string[] arguments)
        {
            var lines = location.GetLineSpan();
            diagnostics.Add(new ScenarioDiagnostic(descriptor.Id, arguments, lines.Path,
                location.SourceSpan.Start, location.SourceSpan.Length,
                new SourceSpan(lines.Path, lines.StartLinePosition.Line, lines.StartLinePosition.Character,
                    lines.EndLinePosition.Line, lines.EndLinePosition.Character)));
        }

        if (attributes.Length == 0)
        {
            if (isScenario && method.Parameters.Length > 0)
            {
                Report(Descriptors.InvalidScenarioCase, syntax.Identifier.GetLocation(), method.Name, "requires at least one ScenarioCase for its parameters");
            }

            return new(cases, diagnostics);
        }

        if (!isScenario || method.Parameters.Length == 0)
        {
            foreach (var attribute in attributes)
            {
                Report(Descriptors.InvalidScenarioCase, attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? syntax.Identifier.GetLocation(),
                    method.Name, "cases require a Scenario method with parameters");
            }

            return new(cases, diagnostics);
        }

        if (method.IsGenericMethod || method.Parameters.Any(p => p.RefKind != RefKind.None || p.IsParams || p.IsOptional)
            || method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>().Count() > 1)
        {
            Report(Descriptors.InvalidScenarioCaseSignature, syntax.Identifier.GetLocation(), method.Name);
            return new(cases, diagnostics);
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            if (attribute.ApplicationSyntaxReference?.GetSyntax() is not AttributeSyntax caseSyntax)
            {
                continue;
            }

            var caseModel = model.Compilation.GetSemanticModel(caseSyntax.SyntaxTree);
            var supplied = caseSyntax.ArgumentList?.Arguments ?? default;
            var key = supplied.Count > 0 ? caseModel.GetConstantValue(supplied[0].Expression).Value as string : null;
            if (string.IsNullOrWhiteSpace(key) || !keys.Add(key!))
            {
                Report(Descriptors.InvalidScenarioCase, caseSyntax.GetLocation(), method.Name, "case names must be nonblank and unique within the method");
                continue;
            }

            if (supplied.Count - 1 != method.Parameters.Length)
            {
                Report(Descriptors.InvalidScenarioCase, caseSyntax.GetLocation(), key!, "argument count must match the scenario parameter count (" + method.Parameters.Length + ")");
                continue;
            }

            var values = new Dictionary<IParameterSymbol, ExpressionSyntax>(SymbolEqualityComparer.Default);
            for (var i = 0; i < method.Parameters.Length; i++)
            {
                var parameter = method.Parameters[i];
                var argument = supplied[i + 1];
                var expression = argument.Expression;
                var constant = caseModel.GetConstantValue(expression);
                var type = caseModel.GetTypeInfo(expression).Type;
                var conversion = caseModel.ClassifyConversion(expression, parameter.Type);
                var supported = constant.HasValue && (constant.Value is null || type is not null && IsConstantType(type));
                var nullability = constant.HasValue && constant.Value is null && parameter.Type.IsReferenceType
                    && parameter.NullableAnnotation == NullableAnnotation.NotAnnotated;
                if (argument.NameColon is not null || argument.NameEquals is not null || !supported
                    || !conversion.IsImplicit || conversion.IsUserDefined || nullability
                    || !model.Compilation.IsSymbolAccessibleWithin(parameter.Type, model.Compilation.Assembly))
                {
                    Report(Descriptors.InvalidScenarioCaseArgument, expression.GetLocation(), parameter.Name,
                        parameter.Type.ToDisplayString(), type?.ToDisplayString() ?? "null");
                    continue;
                }

                ExpressionSyntax value = Constant(constant.Value);
                if (type is not null && constant.Value is not null)
                {
                    value = ParenthesizedExpression(CastExpression(TypeSyntaxFactory.From(type), ParenthesizedExpression(value)));
                }

                var target = TypeSyntaxFactory.From(parameter.Type);
                if (parameter.Type.IsReferenceType && parameter.NullableAnnotation == NullableAnnotation.Annotated)
                {
                    target = NullableType(target);
                }

                values.Add(parameter, ParenthesizedExpression(CastExpression(target, ParenthesizedExpression(value))));
            }

            if (values.Count == method.Parameters.Length)
            {
                cases.Add(new BoundScenarioCase(key!, cases.Count, values));
            }
        }

        return new(cases, diagnostics);
    }

    private static bool IsConstantType(ITypeSymbol type)
        => type.TypeKind == TypeKind.Enum || type.SpecialType is SpecialType.System_String or SpecialType.System_Char
            or SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte
            or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
            or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double;

    private static ExpressionSyntax Constant(object? value)
    {
        if (value is null)
        {
            return LiteralExpression(SyntaxKind.NullLiteralExpression);
        }

        return value switch
        {
            string v => LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(v)),
            char v => LiteralExpression(SyntaxKind.CharacterLiteralExpression, Literal(v)),
            bool v => LiteralExpression(v ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression),
            sbyte v => Literals.Num(v),
            byte v => Literals.Num(v),
            short v => Literals.Num(v),
            ushort v => Literals.Num(v),
            int v => Literals.Num(v),
            uint v => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(v)),
            long.MinValue => Member("long", "MinValue"),
            long v when v < 0 => PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, Constant(-v)),
            long v => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(v)),
            ulong v => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(v)),
            float v when float.IsNaN(v) => Member("float", "NaN"),
            float.PositiveInfinity => Member("float", "PositiveInfinity"),
            float.NegativeInfinity => Member("float", "NegativeInfinity"),
            float v when 1 / v < 0 => PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, Constant(-v)),
            float v => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(v)),
            double v when double.IsNaN(v) => Member("double", "NaN"),
            double.PositiveInfinity => Member("double", "PositiveInfinity"),
            double.NegativeInfinity => Member("double", "NegativeInfinity"),
            double v when 1 / v < 0 => PrefixUnaryExpression(SyntaxKind.UnaryMinusExpression, Constant(-v)),
            double v => LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(v)),
            _ => throw new InvalidOperationException("Unsupported constant type."),
        };
    }

    private static MemberAccessExpressionSyntax Member(string type, string name)
        => MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
            PredefinedType(Token(type == "long" ? SyntaxKind.LongKeyword : type == "float" ? SyntaxKind.FloatKeyword : SyntaxKind.DoubleKeyword)), IdentifierName(name));
}
