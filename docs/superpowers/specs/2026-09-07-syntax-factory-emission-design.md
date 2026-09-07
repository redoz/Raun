# Syntax-factory emission — Design

- **Date:** 2026-09-07
- **Status:** Approved by request (Patrik, 2026-09-07: "replace all string built source generation
  with SyntaxFactory"). Built the same day.
- **Scope:** `src/Raun.Generator` only: `Lowering/Ir.cs`, `ScenarioParser.cs`,
  `DisplayNameBuilder.cs`, `IdentifierReplacer.cs`, `Emit/ScenarioEmitter.cs`, a new
  `Emit/Names.cs` (constant names) and `Lowering/TypeSyntaxFactory.cs` (symbol → `TypeSyntax`).
  Tests in `test/Raun.Generator.Test`.
- **Non-goals:** any change to the generated output's *meaning*; the analyzer; the runtime.
  `EntryPointEmitter`'s fixed template (no variable parts) stays a raw literal.

## Why

Code was being assembled as text in several places — `$"{receiver}.{name}({args})"`,
`$"__inputs.Get<{T}>({i})"`, an interpolated display-name expression escaped by hand, `"using X;"`
lines re-parsed as a compilation unit, and a dozen `ParseExpression`/`ParseTypeName` calls in the
emitter — then re-parsed into syntax the emitter had built with `SyntaxFactory` everywhere else.
Text concatenation is where escaping, precedence and qualification bugs hide. The generator now
builds syntax nodes end to end and never parses text.

## The invariant

Every existing Verify snapshot (`test/Raun.Generator.Test/Snapshots/*.verified.*`) stays
**byte-identical**. That is the proof that behaviour did not change. The one emitted shape that no
snapshot covered — the runtime display-name lambda — changes on purpose (below) and gains its own
snapshot.

## Decisions

### The IR carries syntax, not text

| IR member | Was | Now |
|---|---|---|
| `ParsedStep.InvokeCallText` | `string` | `InvocationExpressionSyntax? InvokeCall` (null for synthetic/teardown) |
| `ParsedStep.ResultTypeFqn` | `string` | `TypeSyntax ResultType` (`object` when there is no result) |
| `ParsedStep.ConditionCoercionType` | `string?` | `TypeSyntax?` |
| `ParsedStep.FormatExpression` | `string?` | `ExpressionSyntax?` |
| `ResourceRoleClaim.Expression` / `SubjectExpressions` | `string` | `ExpressionSyntax` |
| `ParsedScenario.Usings` | `IReadOnlyList<string>` | `IReadOnlyList<UsingDirectiveSyntax>` |
| `ParsedUse.ResourceFqn` | `string` | `TypeSyntax Resource` (+ a `SortKey` string for deterministic order) |
| `VarSource.ElementType` | `string` | `TypeSyntax` |
| replacement maps | `Dictionary<string, string>` | `Dictionary<string, ExpressionSyntax>` |

Strings that are *data* stay strings: ids, display-name templates, verbs, mode names, method names.
Incremental-generator equality is unaffected in practice: the records already held lists, which
compare by reference.

### Symbol → `TypeSyntax` is built, not parsed

`TypeSyntaxFactory.From(ITypeSymbol)` builds the fully-qualified type the way
`SymbolDisplayFormat.FullyQualifiedFormat` would print it:

- named types: `global::` alias, namespace chain, containing types, `GenericName` with type
  arguments; special types as `PredefinedType` (`int`, `string`, `object`, `bool`, …);
  `System.Nullable<T>` as `T?`; a nullable-annotated reference type as `T?`;
- arrays (rank, nested), tuples (with element names when present), type parameters, `dynamic`;
- identifiers that are C# keywords are emitted verbatim (`@class`);
- anything else (pointers, function pointers) throws `NotSupportedException` naming the type; the
  generator surfaces it as RAUN000.

A test compiles a battery of declarations and asserts `From(symbol).ToString()` equals
`symbol.ToDisplayString(FullyQualifiedFormat)` for each, so the builder is pinned to Roslyn's own
printer where it matters.

**Rejected:** keeping one `ParseTypeName(symbol.ToDisplayString(...))` bridge. It is Roslyn's
printer, not hand concatenation, but it is still parsing text, and the builder is small and testable.

### Constant names are built once

`Names.Global("Raun", "Model", "ScenarioDefinition")` yields the `AliasQualifiedName`/`QualifiedName`
chain; `Names.Dotted("Raun", "Generated")` the unqualified form for the namespace declaration and
usings; generic instantiations compose `GenericName`. No `ParseName`/`ParseTypeName` remain.

### The runtime display name is a concatenation

`DisplayNameBuilder` used to build `$"user {(__inputs.Get<…>(0))} exists"` as text with a hand-written
`EscapeForInterpolation`. Roslyn has no factory that escapes interpolated-string text, so the format
expression becomes a `+` chain: `"user " + (__inputs.Get<…>(0)) + " exists"`, where every constant
part is `SyntaxFactory.Literal(string)` (Roslyn escapes) and every hole is a
`ParenthesizedExpression`. When the template starts with a hole the chain starts with `""`, so
left-associativity keeps every `+` a string concatenation even for two adjacent holes. Semantics are
unchanged: both forms call `ToString()` on the value and render `null` as empty.

The constant-folded template (a data string) is untouched.

### Usings are nodes

The parser takes the scenario file's `UsingDirectiveSyntax` nodes (trivia stripped) and builds a
`UsingDirective` for each DSL namespace; the emitter's required usings are built the same way.
Deduplication keys on the node's normalized text. Order is unchanged: required, then the file's,
then the DSL namespaces.

### The header is trivia

`// <auto-generated/>`, `#nullable enable`, `#pragma warning disable CS1591`, `#line hidden` are
built as leading trivia (`Comment`, `NullableDirectiveTrivia`, `PragmaWarningDirectiveTrivia`,
`LineDirectiveTrivia`) attached **after** `NormalizeWhitespace`, and the trailing newline is
end-of-file trivia, so `Emit` returns `unit.ToFullString()` and nothing is concatenated around it.

### What `IdentifierReplacer` does

Same rewriter, but the map holds `ExpressionSyntax`; a replacement is inserted
`.WithTriviaFrom(node)`. The LINQ unroll substitutes `Num(value)` (a `PrefixUnaryExpression` for
negatives). `BuildCallText` becomes `BuildCall`: the original invocation with its argument list
rewritten and `__ctx` appended, outer trivia stripped (what `Trim()` did).

### Guard test

`EmissionDisciplineTests.The_generator_never_parses_source_text` scans `src/Raun.Generator/**/*.cs`
for `ParseExpression(`, `ParseTypeName(`, `ParseName(`, `ParseCompilationUnit(`,
`ParseSyntaxTree(`, `ParseMemberDeclaration(`, `ParseStatement(` as `SyntaxFactory` members, and for
`.ToFullString().Trim()`; it fails if any returns. (The parser's own private `ParseStatement`
methods are matched by the leading `SyntaxFactory.` or `using static` form only — the regex requires
the call to be a `SyntaxFactory` invocation, i.e. not preceded by `this.` or `bool `.)

## Testing

- All eight existing snapshots byte-identical (no `.received.` file may be accepted for them).
- New snapshot `RuntimeName_scenario` (from `SampleSources.RuntimeNameScenario`) pins the concat form.
- `TypeSyntaxFactoryTests`: the display-string parity battery above.
- The guard test.
- Every existing lowering/display-name/PDB test green; `dotnet build Raun.slnx` 0 warnings;
  `dotnet test Raun.slnx` green; both samples unchanged (52/51/1 and 13/13).

## Alternatives considered

- **Build an `InterpolatedStringExpressionSyntax`.** Needs escaped `InterpolatedStringTextToken`
  text, i.e. the hand escaping this change removes. Concatenation has the same semantics and lets
  Roslyn do all escaping.
- **Convert `EntryPointEmitter` to syntax.** It has no variable parts; a fixed template is not
  concatenated code, and the factory version would reformat the snapshot for nothing.
- **Keep `ParseTypeName` for symbol-derived names.** Rejected above.
