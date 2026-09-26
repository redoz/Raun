# Span-form `#line` debug mapping — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Emit C# 10 span-form `#line` directives so a breakpoint on a scenario step's DSL call binds to the developer's exact original call span (column-accurate), and stepping never descends into generated plumbing.

**Architecture:** Default the generated file to `#line hidden` (raw `Header` text), then bracket each step's awaited DSL call inside its `Invoke` lambda with a structured `LineSpanDirectiveTriviaSyntax` mapped to the original invocation span, and `#line hidden` on the `return`. The original span is captured at parse time (`ParsedStep.CallSpan`) and used only for emission; the runtime model stays line-based and untouched. Correctness is proven by a portable-PDB sequence-point test (the generated call maps to the original span; no visible point lands in the generated file), plus snapshots and compile-success.

**Tech Stack:** Roslyn source generator (`Microsoft.CodeAnalysis.CSharp`, baseline 5.3), xUnit, Verify (snapshots), `System.Reflection.Metadata` (PDB reading), jj (commits). Target net10.0, C# `latest`.

**Design spec:** `docs/superpowers/specs/2026-06-03-line-span-mapping-design.md`. Supersedes the *mechanism* in `docs/superpowers/handoffs/2026-06-03-line-directives-handoff.md` (line-only → span).

**Repo gates (apply to every task):**
- Build must report **`0 Warning(s), 0 Error(s)`** (`TreatWarningsAsErrors`, full analyzers). Fix any CA/IDE nit the new helpers raise.
- Tests: `dotnet test Raun.slnx --nologo`. Current baseline: **92 passing**.
- Commits: `jj commit -m "..."`. **No `Co-Authored-By` / tooling trailer.**
- Snapshots: on mismatch Verify writes `*.received.cs` (DiffEngine disabled). **Never blind-accept** — diff received vs verified, confirm the change is exactly expected, then `Move-Item -Force` received→verified and delete any leftover received.

---

## Task 1: SPIKE — confirm directive trivia renders predictably (throwaway)

Resolves the one real unknown before touching the emitter: does `NormalizeWhitespace` render structured `#line` directive trivia on its own line, compiler-legal, with a predictable statement column — and what is the directive's base convention (1-based positions; `charOffset` meaning)? **This file is deleted at the end of the task and never committed.**

**Files:**
- Create (temporary): `test/Raun.Generator.Test/LineSpanSpikeTests.cs`

- [ ] **Step 1: Write the spike**

```csharp
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Raun.Generator.Test;

public class LineSpanSpikeTests(ITestOutputHelper output)
{
    [Fact]
    public void Span_and_hidden_directives_render_on_their_own_lines()
    {
        // Build: { var __r = await M(); return (object?)__r; }
        // with a span directive on the call and #line hidden on the return.
        var call = LocalDeclarationStatement(
            VariableDeclaration(IdentifierName("var"))
                .WithVariables(SingletonSeparatedList(
                    VariableDeclarator(Identifier("__r"))
                        .WithInitializer(EqualsValueClause(
                            AwaitExpression(ParseExpression("M()")))))));

        var span = LineSpanDirectiveTrivia(
            LineDirectivePosition(Literal(78), Literal(33)),   // original (line, col), 1-based guess
            LineDirectivePosition(Literal(78), Literal(70)),
            Literal(20),                                       // charOffset guess
            Literal("Scenario.cs"),
            isActive: true);
        call = call.WithLeadingTrivia(TriviaList(Trivia(span), EndOfLine("\n")));

        var ret = ReturnStatement(
                CastExpression(NullableType(PredefinedType(Token(SyntaxKind.ObjectKeyword))), IdentifierName("__r")))
            .WithLeadingTrivia(TriviaList(
                Trivia(LineDirectiveTrivia(Token(SyntaxKind.HiddenKeyword), isActive: true)), EndOfLine("\n")));

        var block = Block(call, ret).NormalizeWhitespace(eol: "\n");
        var text = block.ToFullString();
        output.WriteLine(text);

        Assert.Contains("#line (78, 33) - (78, 70) 20 \"Scenario.cs\"", text);  // adjust to actual rendering
        Assert.Contains("#line hidden", text);
        // The statement must be intact on its own line, not glued to the directive:
        Assert.Contains("\n", text);
    }
}
```

- [ ] **Step 2: Run the spike**

Run: `dotnet test Raun.slnx --nologo --filter "FullyQualifiedName~LineSpanSpikeTests"`

Read the printed `text`. **Record (you'll paste these into Task 5's commit body):**
1. Exact rendered form of the span directive (spacing, whether positions are `(78, 33)` or `(78,33)`, where `charOffset` and the file sit).
2. Whether the directive sits on its **own line** and the `var __r` statement is intact on the next line, and at what **column** that statement begins (expected 20 — five 4-space levels; confirm).
3. Whether `#line hidden` renders cleanly on its own line.

Adjust the two `Assert.Contains` to match the actual rendering until the test **passes**. If the directive comes out glued/mid-line/illegal (it should not), record the symptom — Task 4 falls back to the raw-text-fragment mechanism (design §4.3).

- [ ] **Step 3: Delete the spike**

```powershell
Remove-Item test\Raun.Generator.Test\LineSpanSpikeTests.cs
```

Run: `dotnet test Raun.slnx --nologo`
Expected: **92 passing** (back to baseline; spike gone). **Do not commit** anything in this task.

---

## Task 2: Path-bearing + PDB harness plumbing

Add the test infrastructure the later tasks need: parse the *user* source with a real path (so spans carry a path), and emit a portable PDB whose sequence points we can read. No production code changes.

**Files:**
- Modify: `test/Raun.Generator.Test/GeneratorHarness.cs`

- [ ] **Step 1: Write the failing test**

Append to `GeneratorSnapshotTests.cs`? No — put harness tests in a new file.

Create `test/Raun.Generator.Test/HarnessPdbTests.cs`:

```csharp
using Xunit;

namespace Raun.Generator.Test;

public class HarnessPdbTests
{
    [Fact]
    public void EmitWithPdb_linear_compiles_and_yields_visible_sequence_points()
    {
        var (errors, pdb) = GeneratorHarness.EmitWithPdb(
            SampleSources.Dsl + SampleSources.LinearScenario, "Scenario.cs");

        Assert.True(errors.IsEmpty, string.Join("; ", errors));
        var points = GeneratorHarness.ReadSequencePoints(pdb);
        Assert.NotEmpty(points);
        Assert.Contains(points, p => !p.IsHidden);     // user code alone produces visible points
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Raun.slnx --nologo --filter "FullyQualifiedName~HarnessPdbTests"`
Expected: FAIL — `EmitWithPdb` / `ReadSequencePoints` / `SeqPoint` do not exist (compile error).

- [ ] **Step 3: Add the harness members**

In `GeneratorHarness.cs`, add `using Microsoft.CodeAnalysis.Emit;` and `using System.Reflection.Metadata;` to the usings. Then add these members to the `GeneratorHarness` class (place after `RunDriver`):

```csharp
/// <summary>Mirrors <see cref="Run"/> but parses the input with a real file path, so spans
/// carry that path (the generator's span-directive branch only fires for path-bearing input).</summary>
public static GeneratorResult RunWithPath(string source, string path, string assemblyName = "ScenarioTests")
{
    var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
    var tree = CSharpSyntaxTree.ParseText(source, parseOptions, path: path);
    var compilation = CSharpCompilation.Create(
        assemblyName + "_" + Guid.NewGuid().ToString("N"),
        [tree],
        References,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable));

    var driver = CSharpGeneratorDriver.Create(
        [new ScenarioGenerator().AsSourceGenerator()],
        parseOptions: parseOptions);
    driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var genDiagnostics);

    var generatedTrees = output.SyntaxTrees.Where(t => t != tree).ToList();
    var generatedSource = string.Join("\n\n", generatedTrees.Select(t => t.ToString()));

    Assembly? assembly = null;
    using var ms = new MemoryStream();
    var emit = output.Emit(ms);
    var emitDiagnostics = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
    if (emit.Success)
    {
        assembly = Assembly.Load(ms.ToArray());
    }

    return new GeneratorResult(genDiagnostics, emitDiagnostics, generatedSource, assembly);
}

/// <summary>Runs the generator over path-bearing source and emits a portable PDB; returns emit
/// errors and the raw PDB bytes for sequence-point inspection.</summary>
public static (ImmutableArray<Diagnostic> Errors, ImmutableArray<byte> Pdb) EmitWithPdb(string source, string path)
{
    var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
    var tree = CSharpSyntaxTree.ParseText(source, parseOptions, path: path);
    var compilation = CSharpCompilation.Create(
        "PdbSnapshot_" + Guid.NewGuid().ToString("N"),
        [tree],
        References,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable));

    var driver = CSharpGeneratorDriver.Create(
        [new ScenarioGenerator().AsSourceGenerator()],
        parseOptions: parseOptions);
    driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

    using var dll = new MemoryStream();
    using var pdbStream = new MemoryStream();
    var emit = output.Emit(dll, pdbStream: pdbStream,
        options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
    var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
    return (errors, [.. pdbStream.ToArray()]);
}

/// <summary>One sequence point read from a portable PDB. Line/column are 1-based;
/// <see cref="IsHidden"/> points have line 0xFEEFEE and no meaningful coordinates.</summary>
public sealed record SeqPoint(string Document, bool IsHidden, int StartLine, int StartColumn, int EndLine, int EndColumn);

public static IReadOnlyList<SeqPoint> ReadSequencePoints(ImmutableArray<byte> pdb)
{
    using var stream = new MemoryStream([.. pdb]);
    using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
    var reader = provider.GetMetadataReader();

    var result = new List<SeqPoint>();
    foreach (var handle in reader.MethodDebugInformation)
    {
        var info = reader.GetMethodDebugInformation(handle);
        if (info.SequencePointsBlob.IsNil)
        {
            continue;
        }

        foreach (var sp in info.GetSequencePoints())
        {
            var doc = sp.Document.IsNil
                ? ""
                : reader.GetString(reader.GetDocument(sp.Document).Name);
            result.Add(new SeqPoint(doc, sp.IsHidden, sp.StartLine, sp.StartColumn, sp.EndLine, sp.EndColumn));
        }
    }

    return result;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Raun.slnx --nologo --filter "FullyQualifiedName~HarnessPdbTests"`
Expected: PASS.

- [ ] **Step 5: Full build + suite**

Run: `dotnet build Raun.slnx --nologo` → `0 Warning(s), 0 Error(s)`.
Run: `dotnet test Raun.slnx --nologo` → **93 passing** (92 + 1 new).

- [ ] **Step 6: Commit**

```powershell
jj commit -m "test: path-bearing + portable-PDB harness plumbing for #line mapping"
```

---

## Task 3: Baseline `#line hidden` + per-return hidden

Make the generated file hidden by default and hide each step's `return`. No span directive yet — the 3 existing snapshots change by **exactly** one baseline `#line hidden` + one `#line hidden` per `return`.

**Files:**
- Modify: `src/Raun.Generator/Emit/ScenarioEmitter.cs` (`Header` const L14; `BuildInvokeLambda` L216-257)
- Modify (re-accept): `test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.{Linear,Tuple,Array}_scenario#RaunScenarios.g.verified.cs`

- [ ] **Step 1: Append `#line hidden` to the `Header` const**

Replace `ScenarioEmitter.cs:14`:

```csharp
    const string Header = "// <auto-generated/>\n#nullable enable\n#pragma warning disable CS1591\n#line hidden\n";
```

- [ ] **Step 2: Put `#line hidden` on the `return` in `BuildInvokeLambda`**

In `BuildInvokeLambda` (L216-257), the method builds `returnStmt` in both the `HasResult` and `else` branches. Attach hidden trivia to each `returnStmt` right before it goes into `bodyStatements`. Add this helper to the class (near the other `static` helpers):

```csharp
static SyntaxTriviaList HiddenTrivia()
    => TriviaList(Trivia(LineDirectiveTrivia(Token(SyntaxKind.HiddenKeyword), isActive: true)), EndOfLine("\n"));
```

Then in the `HasResult` branch, change:

```csharp
            var returnStmt = ReturnStatement(
                CastExpression(
                    NullableType(PredefinedType(Token(SyntaxKind.ObjectKeyword))),
                    IdentifierName("__r")));
```

to append `.WithLeadingTrivia(HiddenTrivia())`:

```csharp
            var returnStmt = ReturnStatement(
                CastExpression(
                    NullableType(PredefinedType(Token(SyntaxKind.ObjectKeyword))),
                    IdentifierName("__r")))
                .WithLeadingTrivia(HiddenTrivia());
```

And in the `else` branch, change:

```csharp
            var returnStmt = ReturnStatement(
                CastExpression(
                    NullableType(PredefinedType(Token(SyntaxKind.ObjectKeyword))),
                    LiteralExpression(SyntaxKind.NullLiteralExpression)));
```

to:

```csharp
            var returnStmt = ReturnStatement(
                CastExpression(
                    NullableType(PredefinedType(Token(SyntaxKind.ObjectKeyword))),
                    LiteralExpression(SyntaxKind.NullLiteralExpression)))
                .WithLeadingTrivia(HiddenTrivia());
```

- [ ] **Step 3: Build**

Run: `dotnet build Raun.slnx --nologo`
Expected: `0 Warning(s), 0 Error(s)`.

- [ ] **Step 4: Regenerate snapshots (they will fail and write `.received.cs`)**

Run: `dotnet test Raun.slnx --nologo --filter "FullyQualifiedName~GeneratorSnapshotTests"`
Expected: 3 snapshot facts FAIL, producing `*.received.cs` next to each verified file.

- [ ] **Step 5: Diff each received vs verified — confirm ONLY the hidden lines changed**

```powershell
Get-ChildItem test\Raun.Generator.Test\Snapshots\*.received.cs | ForEach-Object {
    $verified = $_.FullName -replace '\.received\.cs$', '.verified.cs'
    git diff --no-index $verified $_.FullName
}
```

Confirm the **only** additions are: (a) one `#line hidden` immediately after `#pragma warning disable CS1591`, and (b) one `#line hidden` immediately before each `return (object? )...;`. **No `#line (` span directive** (pathless harness ⇒ no path). If any other line changed, STOP — it's a regression in Step 2.

- [ ] **Step 6: Accept the 3 snapshots**

```powershell
Get-ChildItem test\Raun.Generator.Test\Snapshots\*.received.cs | ForEach-Object {
    Move-Item -Force $_.FullName ($_.FullName -replace '\.received\.cs$', '.verified.cs')
}
```

- [ ] **Step 7: Re-run to confirm green**

Run: `dotnet test Raun.slnx --nologo`
Expected: **93 passing**, no `*.received.cs` left:

```powershell
Get-ChildItem test\Raun.Generator.Test\Snapshots\*.received.cs   # expect: nothing
```

- [ ] **Step 8: Commit**

```powershell
jj commit -m "emit: default generated file to #line hidden; hide step returns"
```

---

## Task 4: Capture the call span + emit the span directive (the core)

Add `ParsedStep.CallSpan`, populate it in the parser, and emit a span-form `#line` directive on each step's awaited call. Driven by the PDB fidelity test, which calibrates `charOffset` and proves column-accurate mapping.

**Files:**
- Modify: `src/Raun.Generator/Lowering/Ir.cs` (add `SourceSpan`; add `ParsedStep.CallSpan`)
- Modify: `src/Raun.Generator/Lowering/ScenarioParser.cs` (add `SpanOf`; populate `CallSpan` at L357-373)
- Modify: `src/Raun.Generator/Emit/ScenarioEmitter.cs` (`BuildInvokeLambda` + a `LineMappedTrivia` helper)
- Create: `test/Raun.Generator.Test/LineMappingPdbTests.cs`
- Modify (re-accept/add): `test/Raun.Generator.Test/GeneratorSnapshotTests.cs` + new `Snapshots/*PathBearing*` verified file

- [ ] **Step 1: Write the failing PDB fidelity test**

Create `test/Raun.Generator.Test/LineMappingPdbTests.cs`:

```csharp
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace Raun.Generator.Test;

public class LineMappingPdbTests(ITestOutputHelper output)
{
    const string Path = "Scenario.cs";

    [Fact]
    public void Generated_step_call_maps_to_original_invocation_span()
    {
        var source = SampleSources.Dsl + SampleSources.LinearScenario;

        // The exact original span of `When.CreateAppointment(patient, slot)` (1-based).
        var expected = InvocationSpan(source, Path, "When", "CreateAppointment");

        var (errors, pdb) = GeneratorHarness.EmitWithPdb(source, Path);
        Assert.True(errors.IsEmpty, string.Join("; ", errors));

        var visible = GeneratorHarness.ReadSequencePoints(pdb).Where(p => !p.IsHidden).ToList();

        // Calibration aid — read these in the test output while tuning charOffset:
        foreach (var p in visible)
        {
            output.WriteLine($"{p.Document} ({p.StartLine},{p.StartColumn})-({p.EndLine},{p.EndColumn})");
        }

        // (1) Plumbing is hidden / remapped: nothing visible lands in the generated file.
        Assert.All(visible, p => Assert.Equal(Path, p.Document));

        // (2) Column-accurate: a visible point starts exactly at the original call (the user's own
        //     scenario method only produces a point at the *statement* column, never at the call).
        Assert.Contains(visible, p =>
            p.Document == Path && p.StartLine == expected.startLine && p.StartColumn == expected.startCol);
    }

    static (int startLine, int startCol, int endLine, int endCol) InvocationSpan(
        string source, string path, string receiver, string method)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path: path);
        var invocation = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(i => i.Expression is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.ValueText: var r },
                Name.Identifier.ValueText: var m,
            } && r == receiver && m == method);
        var s = invocation.GetLocation().GetLineSpan();
        return (s.StartLinePosition.Line + 1, s.StartLinePosition.Character + 1,
                s.EndLinePosition.Line + 1, s.EndLinePosition.Character + 1);   // 1-based
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Raun.slnx --nologo --filter "FullyQualifiedName~LineMappingPdbTests"`
Expected: FAIL on assertion (1) — without span directives, the generated lambdas' statements are visible points in the generated file (`RaunScenarios.g.cs`), so not all visible points have `Document == "Scenario.cs"`.

- [ ] **Step 3: Add `SourceSpan` + `ParsedStep.CallSpan`**

In `src/Raun.Generator/Lowering/Ir.cs`, add the struct (after the `using`, before `ParsedScenario`):

```csharp
/// <summary>The original-source span of a step's DSL call, for span-form #line emission. 0-based
/// (Roslyn LinePosition); the emitter converts to the directive's 1-based form.</summary>
internal readonly record struct SourceSpan(
    string File, int StartLine, int StartChar, int EndLine, int EndChar);
```

Add this property to `ParsedStep` (after `SourceLine`, L27):

```csharp
    /// <summary>Original span of the DSL invocation, for column-accurate #line mapping; null when
    /// the input was parsed without a path (e.g. pathless snapshot harness) ⇒ no directive.</summary>
    public SourceSpan? CallSpan { get; init; }
```

- [ ] **Step 4: Add `SpanOf` and populate `CallSpan` in the parser**

In `src/Raun.Generator/Lowering/ScenarioParser.cs`, add next to `Location` (~L446):

```csharp
    static SourceSpan? SpanOf(SyntaxNode node)
    {
        var s = node.GetLocation().GetLineSpan();
        if (string.IsNullOrEmpty(s.Path))
        {
            return null;
        }

        return new SourceSpan(
            s.Path,
            s.StartLinePosition.Line, s.StartLinePosition.Character,
            s.EndLinePosition.Line, s.EndLinePosition.Character);
    }
```

In `BuildStep`, in the `new ParsedStep { … }` initializer (L357-373), add after `SourceLine = line,`:

```csharp
            CallSpan = SpanOf(invocation),
```

- [ ] **Step 5: Emit the span directive in `BuildInvokeLambda`**

In `ScenarioEmitter.cs`, add a helper next to `HiddenTrivia()`:

```csharp
    // NormalizeWhitespace indents the Invoke-lambda body to this column (5 levels x 4 spaces).
    // charOffset must equal the column where the call STATEMENT begins, so the statement's
    // sequence point (which starts at `var`/`await`) maps to the original invocation start.
    // Confirmed by the Task 1 spike and the LineMappingPdbTests calibration.
    const int LambdaBodyIndent = 20;

    static SyntaxTriviaList LineMappedTrivia(ParsedStep step)
    {
        if (step.CallSpan is not { } span)
        {
            return TriviaList();   // pathless input ⇒ no directive (snapshot determinism)
        }

        var directive = LineSpanDirectiveTrivia(
            LineDirectivePosition(Literal(span.StartLine + 1), Literal(span.StartChar + 1)),   // 1-based
            LineDirectivePosition(Literal(span.EndLine + 1), Literal(span.EndChar + 1)),
            Literal(LambdaBodyIndent),
            Literal(span.File),
            isActive: true);
        return TriviaList(Trivia(directive), EndOfLine("\n"));
    }
```

Then in `BuildInvokeLambda`, attach it to the **call statement** (the first statement) in both branches. In the `HasResult` branch, change the `varDecl` assignment to append the trivia:

```csharp
            var varDecl = LocalDeclarationStatement(
                VariableDeclaration(IdentifierName("var"))
                    .WithVariables(SingletonSeparatedList(
                        VariableDeclarator(Identifier("__r"))
                            .WithInitializer(EqualsValueClause(awaitExpr)))))
                .WithLeadingTrivia(LineMappedTrivia(step));
```

In the `else` branch, change the `awaitStmt`:

```csharp
            var awaitStmt = ExpressionStatement(awaitExpr)
                .WithLeadingTrivia(LineMappedTrivia(step));
```

> **Fallback (only if the Task 1 spike found structured trivia renders unpredictably):** build the lambda body from a controlled raw-text fragment instead — `ParseStatement($"#line ({sl},{sc})-({el},{ec}) {LambdaBodyIndent} {Literal(span.File)}\nvar __r = await {step.InvokeCallText};")` — which makes the column deterministic by construction. The emitted directive text is the contract either way. Record the choice in the Step 11 commit body.

- [ ] **Step 6: Build**

Run: `dotnet build Raun.slnx --nologo`
Expected: `0 Warning(s), 0 Error(s)`. (If CA flags `LambdaBodyIndent` or the helper, address per analyzer guidance — e.g. concrete return types.)

- [ ] **Step 7: Run the PDB fidelity test and calibrate `charOffset`**

Run: `dotnet test Raun.slnx --nologo --filter "FullyQualifiedName~LineMappingPdbTests"`

- If **PASS** → `charOffset = 20` is correct; continue.
- If assertion (2) FAILS → read the test output's printed visible points. Find the point on the `expected` line whose column is *closest* to (but not equal to) `expected.startCol`. The difference tells you the calibration: **`LambdaBodyIndent += (expected.startCol - actualStartColumn)`**. Adjust the const, rebuild, re-run. Converges in one step (the only unknown is `NormalizeWhitespace`'s exact indent/base convention).
- If assertion (1) FAILS (a visible point still in `RaunScenarios.g.cs`) → a generated statement isn't covered: confirm the baseline `#line hidden` (Task 3) is present and the directive attached to the call statement; the `return` is hidden. Re-check Task 3 + Step 5.

- [ ] **Step 8: Add the path-bearing snapshot fact**

In `GeneratorSnapshotTests.cs`, add:

```csharp
    [Fact]
    public Task PathBearing_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.Dsl + SampleSources.LinearScenario, "Scenario.cs"))
            .UseDirectory("Snapshots");
```

This requires `RunDriver` to accept a path. In `GeneratorHarness.cs`, change `RunDriver`'s signature and its `ParseText` call:

```csharp
    public static GeneratorDriver RunDriver(string source, string? path = null)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions, path: path ?? "");
        // … rest unchanged …
```

(The existing 3 snapshot facts call `RunDriver(source)` with no path → `path ?? ""` is pathless → their snapshots are unaffected.)

- [ ] **Step 9: Generate + inspect + accept the new snapshot**

Run: `dotnet test Raun.slnx --nologo --filter "FullyQualifiedName~GeneratorSnapshotTests.PathBearing_scenario"`
Expected: FAIL — writes `GeneratorSnapshotTests.PathBearing_scenario#RaunScenarios.g.received.cs`.

Inspect the received file. Confirm each of the 4 steps shows, on its own line immediately before the `var __r = await …` / `await …` line:

```
#line (76, C1) - (76, C2) 20 "Scenario.cs"   // Given.PatientExists
#line (77, C1) - (77, C2) 20 "Scenario.cs"   // Given.AvailableSlot
#line (78, C1) - (78, C2) 20 "Scenario.cs"   // When.CreateAppointment
#line (79, C1) - (79, C2) 20 "Scenario.cs"   // Then.AppointmentExists (void)
```

(exact column numbers `C1`/`C2` and directive spacing as rendered; lines 76-79 match the existing snapshots' `SourceLine` values) and `#line hidden` before each `return`. No other differences vs the Linear snapshot beyond the path-bearing directives. Then accept:

```powershell
$r = "test\Raun.Generator.Test\Snapshots\GeneratorSnapshotTests.PathBearing_scenario#RaunScenarios.g.received.cs"
Move-Item -Force $r ($r -replace '\.received\.cs$', '.verified.cs')
```

- [ ] **Step 10: Build + full suite**

Run: `dotnet build Raun.slnx --nologo` → `0 Warning(s), 0 Error(s)`.
Run: `dotnet test Raun.slnx --nologo` → **95 passing** (93 + PDB fidelity + PathBearing snapshot). Confirm no `*.received.cs` remain.

- [ ] **Step 11: Commit**

```powershell
jj commit -m @'
emit: span-form #line directives map step calls to original source

Capture each DSL call's full span (ParsedStep.CallSpan) and emit a
LineSpanDirectiveTrivia on the awaited call inside the Invoke lambda, mapping
it to the exact original invocation span; returns stay #line hidden. charOffset
= generated lambda-body indent so the statement sequence point lands on the
original call start. Proven by a portable-PDB sequence-point test (no visible
point in the generated file; the call maps column-accurately) plus a
path-bearing snapshot. Pathless input still emits no span directive.

Spike outcome: <structured trivia | raw-text fallback>; charOffset calibrated to <N>.
'@
```

---

## Task 5: Compile-success fact + final verification

Lock the populated-path compile path with an explicit assertion, run the manual-checklist reminder, and update the docs.

**Files:**
- Modify: `test/Raun.Generator.Test/GeneratorSnapshotTests.cs` (or `LineMappingPdbTests.cs`) — add compile-success fact
- Modify: `docs/superpowers/handoffs/2026-06-03-line-directives-handoff.md` (supersede note)

- [ ] **Step 1: Write the compile-success fact**

Add to `LineMappingPdbTests.cs`:

```csharp
    [Fact]
    public void PathBearing_scenario_compiles()
    {
        var result = GeneratorHarness.RunWithPath(
            SampleSources.Dsl + SampleSources.LinearScenario, "Scenario.cs");
        result.AssertCompiles();
    }
```

- [ ] **Step 2: Run it**

Run: `dotnet test Raun.slnx --nologo --filter "FullyQualifiedName~LineMappingPdbTests"`
Expected: PASS (the C# compiler accepts the span directives with a real path).

- [ ] **Step 3: Spot-check snapshot invariants**

Confirm the 3 pathless snapshots have **zero** span directives and the path-bearing one has them:

```powershell
Select-String -Path test\Raun.Generator.Test\Snapshots\*.verified.cs -Pattern '#line \(' |
    Select-Object Filename, LineNumber, Line
```

Expected: matches appear **only** in `…PathBearing_scenario#RaunScenarios.g.verified.cs` (4 of them); the Linear/Tuple/Array snapshots show none. Also confirm all four snapshots contain `#line hidden`.

- [ ] **Step 4: Full suite**

Run: `dotnet test Raun.slnx --nologo` → **96 passing** (95 + compile-success). Build `0 Warning(s), 0 Error(s)`.

- [ ] **Step 5: Add the supersede note to the old handoff**

At the top of `docs/superpowers/handoffs/2026-06-03-line-directives-handoff.md`, under the **Status** line, add:

```markdown
> **Superseded (2026-06-03):** implemented with span-form `#line` mapping instead of line-only.
> See `docs/superpowers/specs/2026-06-03-line-span-mapping-design.md` and
> `docs/superpowers/plans/2026-06-03-line-span-mapping.md`.
```

- [ ] **Step 6: Commit**

```powershell
jj commit -m "test: assert path-bearing generated code compiles; note handoff superseded"
```

- [ ] **Step 7: Manual debugger checklist (human, once — not automated)**

Per design §8, in an IDE against the sample `AppointmentTests`:
1. Breakpoint on a `When.CreateAppointment(...)` line in the original `*Tests.cs` → binds and hits **on that original call**, highlight covers the call span; not in `RaunScenarios.g.cs`.
2. Step Over / Step Into → moves between original DSL call sites; never descends into generated plumbing.
3. Breakpoint on a void step (`Then.AppointmentExists(...)`) → binds and hits.
4. `__r`/`__inputs` in Locals (instead of the user's names) is expected.

---

## Self-review (completed during planning)

- **Spec coverage:** §3 parser/IR → Task 4 Steps 3-4. §4 emitter (baseline, span directive, omission) → Task 3 + Task 4 Step 5. §5 spike → Task 1. §6 verification (compile-success → Task 5; snapshots → Tasks 3-4; PDB → Task 4) all present. §7 non-goals respected (runtime model untouched; no arg-hoist; no multi-line). §9 conventions in the gates block.
- **Placeholders:** none — every step has concrete code/commands. Column numbers in the path-bearing snapshot and the exact `charOffset` are deliberately calibrated against real output (Task 4 Steps 7/9), which is correct for a value only the compiler/formatter can confirm; the calibration procedure is explicit.
- **Type consistency:** `SeqPoint`, `SourceSpan`, `CallSpan`, `LineMappedTrivia`, `HiddenTrivia`, `LambdaBodyIndent`, `RunWithPath`, `EmitWithPdb`, `ReadSequencePoints`, `RunDriver(source, path)` used consistently across tasks.
- **Test count:** 92 → 93 (Task 2) → 95 (Task 4: +PDB fidelity, +PathBearing snapshot) → 96 (Task 5: +compile-success). Spike (Task 1) is throwaway and not counted.
