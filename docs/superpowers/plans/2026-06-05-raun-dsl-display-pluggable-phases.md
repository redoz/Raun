# Raun DSL display names, pluggable phases & RAUN000 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let scenario steps read with their phase word and let a scenario class be renamed in the runner, make phase markers pluggable via a `Raun.IPhase` marker interface, and turn unexpected generator/analyzer throws into a clean `RAUN000` diagnostic.

**Architecture:** Three of the four changes are framework-level and follow the existing `[Scenario]`/`[StepName]` pipeline (attribute → `ScenarioParser` → `ParsedScenario` → `ScenarioEmitter` → `ScenarioDefinition` → `Raun.Mtp` consumers). Phase recognition is centralized in `SymbolHelpers.PhaseOf`, so pluggability is a single-method change plus a shape change on `Given/When/Then` (static classes cannot implement interfaces — compiler-verified). The fourth change wraps the generator's parse/emit stages and the analyzer's per-method analysis in delegate-driven safety helpers that are unit-testable without forcing the real code to throw.

**Tech Stack:** C# 14 / .NET 10, Roslyn incremental source generator + analyzer (`netstandard2.0`), xUnit, VerifyXunit snapshots, Microsoft.Testing.Platform. Version control is **jj** (Jujutsu).

---

## Conventions for every task

- **Build (whole repo):** `dotnet build` from `C:\dev\raun`.
- **Run a test project:** `dotnet test test\<Project>\<Project>.csproj` (these are the canonical full-project runs used in the steps; they always work regardless of runner). To narrow while iterating you may append `--filter "FullyQualifiedName~<Name>"`.
- **Commit (jj):** `jj commit -m "<message>"` — this finalizes the current working-copy change and starts a fresh one. jj auto-tracks files; there is **no** `git add`. Do **not** add `Co-Authored-By`/tooling trailers (project rule).
- Each task is TDD: write the failing test, watch it fail, implement minimally, watch it pass, then commit.

## File structure (what each touched file is responsible for)

- `src/Raun/Phases.cs` — the `IPhase` marker + the built-in `Given/When/Then` markers.
- `src/Raun/Model/ScenarioDefinition.cs` — adds `ClassDisplayName` (carries the class node label to the runtime).
- `src/Raun.Generator/Lowering/SymbolHelpers.cs` — `PhaseOf` recognizes any `IPhase` implementer.
- `src/Raun.Generator/Lowering/AttributeReader.cs` — reads `[DisplayName]` off the declaring type.
- `src/Raun.Generator/Lowering/Ir.cs` — `ParsedScenario.ClassDisplayName`.
- `src/Raun.Generator/Lowering/ScenarioParser.cs` — populates `ClassDisplayName`.
- `src/Raun.Generator/Emit/ScenarioEmitter.cs` — emits `ClassDisplayName`.
- `src/Raun.Generator/GeneratorSafety.cs` (new) — `SafeParse`/`SafeEmit`/`Describe` exception-wrapping helpers.
- `src/Raun.Generator/ScenarioGenerator.cs` — wires the safety helpers into the pipeline.
- `src/Raun.Generator/Analysis/Descriptors.cs` — `RAUN000` + reworded phase-marker messages.
- `src/Raun.Generator/Analysis/ScenarioAnalyzer.cs` — wraps `AnalyzeMethod`; supports `RAUN000`.
- `src/Raun.Generator/AnalyzerReleases.Unshipped.md` — release rows.
- `src/Raun.Mtp/ScenarioTestIdentity.cs` + `RaunDiscoverer.cs` + `RaunStepReporter.cs` — use the class display name.
- `samples/AppointmentTests/AppointmentDsl.cs` + `Scenarios.cs` — phase words + `[DisplayName]`.

---

## Task 1: Pluggable phase markers via `IPhase`

**Files:**
- Modify: `src/Raun/Phases.cs`
- Modify: `src/Raun.Generator/Lowering/SymbolHelpers.cs`
- Modify: `src/Raun.Generator/Analysis/Descriptors.cs`
- Modify: `src/Raun.Generator/AnalyzerReleases.Unshipped.md`
- Test: `test/Raun.Generator.Test/PluggablePhaseTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `test/Raun.Generator.Test/PluggablePhaseTests.cs`:

```csharp
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>A custom type implementing Raun.IPhase is recognised as a phase marker, just like the
/// built-in Given/When/Then, and its type name becomes the step's phase label.</summary>
public class PluggablePhaseTests
{
    const string CustomPhaseSource =
        """
        using System.Threading.Tasks;
        using Raun;

        namespace Demo;

        public sealed class Arrange : IPhase { private Arrange() { } }

        public sealed record Widget(int Id);

        public static class CustomDsl
        {
            extension(Arrange)
            {
                [StepName("a widget exists")]
                public static async Task<Widget> WidgetExists()
                {
                    await Task.Yield();
                    return new Widget(1);
                }
            }
        }

        public static class CustomScenarios
        {
            [Scenario("custom phase")]
            public static async Task S()
            {
                await Arrange.WidgetExists();
            }
        }
        """;

    [Fact]
    public void Custom_IPhase_marker_is_recognised_and_names_the_phase()
    {
        var result = GeneratorHarness.Run(CustomPhaseSource);
        result.AssertCompiles();

        var def = Assert.Single(result.Definitions());
        var node = Assert.Single(def.Nodes);
        Assert.Equal("Arrange", node.Phase);
        Assert.Equal("a widget exists", node.DisplayNameTemplate);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj --filter "FullyQualifiedName~PluggablePhaseTests"`
Expected: FAIL. Either the source fails to compile (no `Raun.IPhase` type yet) or `Definitions()` is empty because `PhaseOf` does not recognise `Arrange`.

- [ ] **Step 3: Add the `IPhase` marker and reshape the built-in markers**

Replace the body of `src/Raun/Phases.cs` with:

```csharp
namespace Raun;

// Marker "phase" types. Domain DSLs hang steps off these with C# 14 static extension
// members, e.g. `extension(Given) { public static Task<Patient> PatientExists(...) }`.
// They carry no behaviour and are never instantiated; they exist only to give the
// Given/When/Then call sites a type to extend. Custom phases are any type implementing
// IPhase — the generator recognises them and uses the type name as the phase label.

/// <summary>Marker interface for phase types. Any type implementing it can host DSL steps and is
/// recognised by the generator; the type's name becomes the step's phase label.</summary>
public interface IPhase { }

/// <summary>Phase marker for arrange / precondition steps.</summary>
public sealed class Given : IPhase { private Given() { } }

/// <summary>Phase marker for the action under test.</summary>
public sealed class When : IPhase { private When() { } }

/// <summary>Phase marker for assertions / postconditions.</summary>
public sealed class Then : IPhase { private Then() { } }
```

- [ ] **Step 4: Recognize any `IPhase` implementer in `PhaseOf`**

In `src/Raun.Generator/Lowering/SymbolHelpers.cs`, add `using System.Linq;` at the top of the using block, then replace the `PhaseOf` method:

```csharp
    /// <summary>Returns the receiver type's name if it implements <c>Raun.IPhase</c> (the built-in
    /// Given/When/Then markers do; so does any user-defined marker), else null.</summary>
    public static string? PhaseOf(ExpressionSyntax receiver, SemanticModel model)
    {
        if (model.GetSymbolInfo(receiver).Symbol is not INamedTypeSymbol type)
        {
            return null;
        }

        var isPhase = type.AllInterfaces.Any(i =>
            i.Name == "IPhase"
            && i.ContainingNamespace?.ToDisplayString(NoGlobal) == "Raun");

        return isPhase ? type.Name : null;
    }
```

- [ ] **Step 5: Reword the phase-marker diagnostics**

In `src/Raun.Generator/Analysis/Descriptors.cs`, update three descriptors so their text reflects pluggable markers. Replace `UnsupportedStatement`, `NotADslCall`, and `InvalidGroupElement` with:

```csharp
    public static readonly DiagnosticDescriptor UnsupportedStatement = new(
        "RAUN002",
        "Unsupported scenario statement",
        "Scenario statements must be an awaited phase-marker call (Given/When/Then, or any type implementing Raun.IPhase), an awaited tuple, or an awaited array of such calls",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
```

```csharp
    public static readonly DiagnosticDescriptor NotADslCall = new(
        "RAUN004",
        "Scenario step must be a phase-marker call",
        "Scenario steps must call a static extension member on a phase marker (Given/When/Then, or any type implementing Raun.IPhase)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
```

```csharp
    public static readonly DiagnosticDescriptor InvalidGroupElement = new(
        "RAUN006",
        "Parallel group element must be a phase-marker call",
        "Every element of a tuple/array parallel group must be a phase-marker call (Given/When/Then, or any type implementing Raun.IPhase)",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
```

- [ ] **Step 6: Keep the analyzer release notes in sync with the new titles**

In `src/Raun.Generator/AnalyzerReleases.Unshipped.md`, update the RAUN004 and RAUN006 rows' Notes to match the new titles (leave RAUN002's Notes — its title is unchanged):

```
RAUN004 | Raun.Usage | Error | Scenario step must be a phase-marker call
RAUN006 | Raun.Usage | Error | Parallel group element must be a phase-marker call
```

- [ ] **Step 7: Run the new test to verify it passes**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj --filter "FullyQualifiedName~PluggablePhaseTests"`
Expected: PASS.

- [ ] **Step 8: Run the full suites to confirm Given/When/Then still work**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj`
Then: `dotnet test test\Raun.Test\Raun.Test.csproj`
Then: `dotnet test test\Raun.Mtp.Test\Raun.Mtp.Test.csproj`
Expected: all PASS (the snapshot tests still pass — this task does not change emitted output).

- [ ] **Step 9: Commit**

```bash
jj commit -m "feat(raun): make phase markers pluggable via Raun.IPhase"
```

---

## Task 2: `ScenarioDefinition.ClassDisplayName` + `ScenarioTestIdentity` honors it

**Files:**
- Modify: `src/Raun/Model/ScenarioDefinition.cs`
- Modify: `src/Raun.Mtp/ScenarioTestIdentity.cs`
- Modify: `src/Raun.Mtp/RaunDiscoverer.cs:57`
- Modify: `src/Raun.Mtp/RaunStepReporter.cs:163`
- Test: `test/Raun.Mtp.Test/ScenarioTestIdentityTests.cs`

- [ ] **Step 1: Write the failing test**

In `test/Raun.Mtp.Test/ScenarioTestIdentityTests.cs`, add two facts to the class:

```csharp
    [Fact]
    public void Create_uses_the_class_display_name_as_the_type_when_provided()
    {
        var id = ScenarioTestIdentity.Create(
            "MyApp.Bookings.Book", "customer books an appointment", "Appointment booking");

        Assert.Equal("MyApp", id.Namespace);
        Assert.Equal("Appointment booking", id.TypeName);
        Assert.Equal("customer books an appointment", id.MethodName);
    }

    [Fact]
    public void Create_falls_back_to_the_fqn_type_when_class_display_name_is_null_or_empty()
    {
        var nullName = ScenarioTestIdentity.Create("MyApp.Bookings.Book", "scenario", null);
        var emptyName = ScenarioTestIdentity.Create("MyApp.Bookings.Book", "scenario", "");

        Assert.Equal("Bookings", nullName.TypeName);
        Assert.Equal("Bookings", emptyName.TypeName);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test\Raun.Mtp.Test\Raun.Mtp.Test.csproj --filter "FullyQualifiedName~ScenarioTestIdentityTests"`
Expected: FAIL to compile — `Create` currently takes only two arguments.

- [ ] **Step 3: Add `ClassDisplayName` to the model**

In `src/Raun/Model/ScenarioDefinition.cs`, immediately after the `MethodName` property add:

```csharp
    /// <summary>Optional display name for the scenario's declaring class (from a <c>[DisplayName]</c>
    /// attribute); when null the runner uses the real type name.</summary>
    public string? ClassDisplayName { get; init; }
```

- [ ] **Step 4: Make `Create` honor the class display name**

In `src/Raun.Mtp/ScenarioTestIdentity.cs`, replace the `Create` method with:

```csharp
    /// <summary>
    /// Builds the method-identity property for a scenario: namespace and type are derived from
    /// <paramref name="methodFullName"/> (the scenario method's FQN), but the method node is the human
    /// <paramref name="scenarioDisplayName"/> so a runner groups steps under the scenario name. When
    /// <paramref name="classDisplayName"/> is non-empty it overrides the derived type name.
    /// </summary>
    public static TestMethodIdentifierProperty Create(
        string methodFullName, string scenarioDisplayName, string? classDisplayName = null)
    {
        ArgumentNullException.ThrowIfNull(scenarioDisplayName);
        Split(methodFullName, out var @namespace, out var typeName, out _);

        var type = string.IsNullOrEmpty(classDisplayName) ? typeName : classDisplayName!;

        // Positional ctor args (assembly, namespace, type, method, method-arity, parameter-types,
        // return-type) — matching xunit.v3's MTP bridge. Scenarios are non-generic, parameterless
        // (the DSL drives them), so arity 0 / no parameters / void.
        return new TestMethodIdentifierProperty(
            AssemblyFullName,
            @namespace,
            type,
            scenarioDisplayName,
            0,
            [],
            VoidReturnTypeName);
    }
```

- [ ] **Step 5: Pass the class display name from both call sites**

In `src/Raun.Mtp/RaunDiscoverer.cs` line ~57, change:

```csharp
        node.Properties.Add(ScenarioTestIdentity.Create(definition.MethodName, definition.DisplayName, definition.ClassDisplayName));
```

In `src/Raun.Mtp/RaunStepReporter.cs` line ~163, change:

```csharp
        testNode.Properties.Add(ScenarioTestIdentity.Create(definition.MethodName, definition.DisplayName, definition.ClassDisplayName));
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test test\Raun.Mtp.Test\Raun.Mtp.Test.csproj --filter "FullyQualifiedName~ScenarioTestIdentityTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
jj commit -m "feat(mtp): honor a scenario class display name in test identity"
```

---

## Task 3: Generator reads `[DisplayName]` into `ClassDisplayName` (and regenerate snapshots)

**Files:**
- Modify: `src/Raun.Generator/Lowering/AttributeReader.cs`
- Modify: `src/Raun.Generator/Lowering/Ir.cs`
- Modify: `src/Raun.Generator/Lowering/ScenarioParser.cs`
- Modify: `src/Raun.Generator/Emit/ScenarioEmitter.cs`
- Test: `test/Raun.Generator.Test/ClassDisplayNameTests.cs` (create)
- Modify (regenerate): `test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.Linear_scenario#RaunScenarios.g.verified.cs`
- Modify (regenerate): `test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.Tuple_scenario#RaunScenarios.g.verified.cs`
- Modify (regenerate): `test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.Array_scenario#RaunScenarios.g.verified.cs`
- Modify (regenerate): `test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.PathBearing_scenario#RaunScenarios.g.verified.cs`

- [ ] **Step 1: Write the failing test**

Create `test/Raun.Generator.Test/ClassDisplayNameTests.cs`:

```csharp
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>A [DisplayName] on the scenario's declaring class flows into
/// ScenarioDefinition.ClassDisplayName; without it the value is null.</summary>
public class ClassDisplayNameTests
{
    const string WithDisplayName =
        """

        [System.ComponentModel.DisplayName("Appointment booking")]
        public static class NamedScenarios
        {
            [Scenario("booking")]
            public static async Task Booking()
            {
                var patient = await Given.PatientExists("Jane");
                await Then.Greet(patient);
            }
        }
        """;

    [Fact]
    public void DisplayName_attribute_sets_ClassDisplayName()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + WithDisplayName);
        result.AssertCompiles();

        var def = Assert.Single(result.Definitions());
        Assert.Equal("Appointment booking", def.ClassDisplayName);
    }

    [Fact]
    public void Absent_DisplayName_leaves_ClassDisplayName_null()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinearScenario);
        result.AssertCompiles();

        var def = Assert.Single(result.Definitions());
        Assert.Null(def.ClassDisplayName);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj --filter "FullyQualifiedName~ClassDisplayNameTests"`
Expected: FAIL — `ScenarioDefinition.ClassDisplayName` is always null (the generator never sets it), so `DisplayName_attribute_sets_ClassDisplayName` fails on the assertion.

- [ ] **Step 3: Read the attribute off the declaring type**

In `src/Raun.Generator/Lowering/AttributeReader.cs`, add this method to the class:

```csharp
    public static string? ClassDisplayName(INamedTypeSymbol type)
    {
        var attr = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "DisplayNameAttribute");
        if (attr is { ConstructorArguments.Length: > 0 } && attr.ConstructorArguments[0].Value is string name)
        {
            return name;
        }

        return null;
    }
```

- [ ] **Step 4: Carry it on the IR**

In `src/Raun.Generator/Lowering/Ir.cs`, add a property to `ParsedScenario` (next to `DisplayName`):

```csharp
    public string? ClassDisplayName { get; init; }
```

- [ ] **Step 5: Populate it in the parser**

In `src/Raun.Generator/Lowering/ScenarioParser.cs`, in the `return new ParsedScenario { ... }` object initializer inside `Parse()`, add after the `DisplayName = ...` line:

```csharp
            ClassDisplayName = AttributeReader.ClassDisplayName(_method.ContainingType),
```

- [ ] **Step 6: Emit it into the definition**

In `src/Raun.Generator/Emit/ScenarioEmitter.cs`, in `BuildScenarioBuilder`'s `ScenarioDefinition` initializer list, add a line right after `Set("MethodName", Lit(scenario.MethodFullName)),`:

```csharp
                Set("ClassDisplayName", Lit(scenario.ClassDisplayName)),
```

- [ ] **Step 7: Run the unit test to verify it passes**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj --filter "FullyQualifiedName~ClassDisplayNameTests"`
Expected: PASS.

The emitter now also writes a `ClassDisplayName = null,` line into every `ScenarioDefinition` initializer, so the four lowering snapshots are stale. Regenerate them before committing so the commit stays green. (DiffEngine is disabled in `VerifyConfig`, so Verify writes `*.received.cs` next to the `*.verified.cs` on mismatch.)

- [ ] **Step 8: Run the snapshot tests to see them fail**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj --filter "FullyQualifiedName~GeneratorSnapshotTests"`
Expected: FAIL for `Linear_scenario`, `Tuple_scenario`, `Array_scenario`, `PathBearing_scenario` (the `EntryPoint` snapshot still passes — it contains no `ScenarioDefinition`). Each failure writes a `*.received.cs` file.

- [ ] **Step 9: Confirm the only diff is the new line**

Confirm the **only** change in each received snapshot is one added `ClassDisplayName = null,` line just after `MethodName = "...",`:

```bash
jj diff --git
```

(There should be no other differences. If there are, stop and investigate — the emitter change leaked something unexpected.)

- [ ] **Step 10: Accept the snapshots**

Overwrite each `*.verified.cs` with its `*.received.cs` and remove the received files. In PowerShell, from `C:\dev\raun\test\Raun.Generator.Test\Snapshots`:

```powershell
Get-ChildItem -Filter '*.received.cs' | ForEach-Object {
    $verified = $_.FullName -replace '\.received\.cs$', '.verified.cs'
    Move-Item -Force $_.FullName $verified
}
```

- [ ] **Step 11: Re-run the whole generator suite to verify green**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj`
Expected: PASS (all snapshots accepted; unit tests green).

- [ ] **Step 12: Commit**

```bash
jj commit -m "feat(generator): read [DisplayName] on a scenario class into ClassDisplayName"
```

---

## Task 4: RAUN000 in the generator (parse + emit safety net)

**Files:**
- Modify: `src/Raun.Generator/Raun.Generator.csproj` (add `InternalsVisibleTo`)
- Create: `src/Raun.Generator/GeneratorSafety.cs`
- Modify: `src/Raun.Generator/Analysis/Descriptors.cs` (add RAUN000)
- Modify: `src/Raun.Generator/AnalyzerReleases.Unshipped.md` (add RAUN000 row)
- Modify: `src/Raun.Generator/ScenarioGenerator.cs` (wire safety helpers)
- Test: `test/Raun.Generator.Test/GeneratorSafetyTests.cs` (create)

- [ ] **Step 1: Make generator internals visible to the test project**

In `src/Raun.Generator/Raun.Generator.csproj`, add an `ItemGroup`:

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="Raun.Generator.Test" />
  </ItemGroup>
```

- [ ] **Step 2: Write the failing test**

Create `test/Raun.Generator.Test/GeneratorSafetyTests.cs`:

```csharp
using System;
using Raun.Generator;
using Raun.Generator.Lowering;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>The parse/emit safety helpers turn an unexpected throw into a reportable error instead
/// of crashing the generator, and pass successful results through untouched.</summary>
public class GeneratorSafetyTests
{
    [Fact]
    public void SafeParse_wraps_an_exception_as_an_error_result()
    {
        var result = GeneratorSafety.SafeParse(
            () => throw new InvalidOperationException("boom"), "Scenarios.cs", 12);

        Assert.Null(result.Scenario);
        Assert.Equal("Scenarios.cs", result.File);
        Assert.Equal(12, result.Line);
        Assert.Contains("boom", result.Error);
        Assert.Contains("InvalidOperationException", result.Error);
    }

    [Fact]
    public void SafeParse_passes_a_successful_parse_through()
    {
        var scenario = new ParsedScenario { DisplayName = "ok" };

        var result = GeneratorSafety.SafeParse(() => scenario, "x", 1);

        Assert.Same(scenario, result.Scenario);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SafeEmit_wraps_an_exception_as_an_error()
    {
        var (source, error) = GeneratorSafety.SafeEmit(() => throw new InvalidOperationException("kaboom"));

        Assert.Null(source);
        Assert.Contains("kaboom", error);
    }

    [Fact]
    public void SafeEmit_passes_emitted_source_through()
    {
        var (source, error) = GeneratorSafety.SafeEmit(() => "generated");

        Assert.Equal("generated", source);
        Assert.Null(error);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj --filter "FullyQualifiedName~GeneratorSafetyTests"`
Expected: FAIL to compile — `GeneratorSafety` does not exist yet.

- [ ] **Step 4: Create the safety helpers**

Create `src/Raun.Generator/GeneratorSafety.cs`:

```csharp
using System;
using Raun.Generator.Lowering;

namespace Raun.Generator;

/// <summary>The outcome of safely parsing one scenario: either the parsed <see cref="Scenario"/>, or
/// an <see cref="Error"/> (exception text) plus the originating method's <see cref="File"/>/<see cref="Line"/>
/// to report as RAUN000.</summary>
internal readonly record struct ScenarioResult(ParsedScenario? Scenario, string? Error, string? File, int Line);

/// <summary>
/// Wraps the generator's parse and emit stages so an unexpected throw becomes a RAUN000 diagnostic
/// instead of crashing the generator (CS8785). Delegate-driven, so the wrapping behaviour is
/// unit-testable without forcing the real parser/emitter to throw.
/// </summary>
internal static class GeneratorSafety
{
    public static ScenarioResult SafeParse(Func<ParsedScenario?> parse, string? file, int line)
    {
        try
        {
            return new ScenarioResult(parse(), null, null, 0);
        }
        catch (Exception ex)
        {
            return new ScenarioResult(null, Describe(ex), file, line);
        }
    }

    public static (string? Source, string? Error) SafeEmit(Func<string> emit)
    {
        try
        {
            return (emit(), null);
        }
        catch (Exception ex)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>A compact, single-line description of an exception for a diagnostic message.</summary>
    public static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj --filter "FullyQualifiedName~GeneratorSafetyTests"`
Expected: PASS.

- [ ] **Step 6: Add the RAUN000 descriptor**

In `src/Raun.Generator/Analysis/Descriptors.cs`, add at the top of the descriptor list (before `MustBeAsyncTask`):

```csharp
    public static readonly DiagnosticDescriptor UnhandledException = new(
        "RAUN000",
        "Unhandled exception in Raun generator",
        "Raun failed to process a scenario: {0}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
```

- [ ] **Step 7: Register RAUN000 in the analyzer release file**

In `src/Raun.Generator/AnalyzerReleases.Unshipped.md`, add a row at the top of the table (before RAUN001):

```
RAUN000 | Raun.Usage | Error | Unhandled exception in Raun generator
```

- [ ] **Step 8: Wire the helpers into the generator pipeline**

In `src/Raun.Generator/ScenarioGenerator.cs`:

(a) Add `using System.Collections.Generic;` and `using Raun.Generator.Analysis;` to the using block.

(b) Replace the `scenarios` provider + first `RegisterSourceOutput` (the manifest output) with:

```csharp
        var scenarios = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Raun.ScenarioAttribute",
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => Transform(ctx))
            .Where(static result => result is not null)
            .Collect();

        context.RegisterSourceOutput(scenarios, static (spc, items) =>
        {
            var parsed = new List<ParsedScenario>();
            foreach (var result in items)
            {
                if (result is not { } r)
                {
                    continue;
                }

                if (r.Error is not null)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.UnhandledException, MakeLocation(r.File, r.Line), r.Error));
                }
                else if (r.Scenario is not null)
                {
                    parsed.Add(r.Scenario);
                }
            }

            if (parsed.Count == 0)
            {
                return;
            }

            var (source, error) = GeneratorSafety.SafeEmit(() => ScenarioEmitter.Emit(parsed));
            if (error is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Descriptors.UnhandledException, Location.None, error));
                return;
            }

            spc.AddSource("RaunScenarios.g.cs", SourceText.From(source!, Encoding.UTF8));
        });
```

(c) Replace the entry-point `RegisterSourceOutput` with a try/catch-wrapped emit:

```csharp
        context.RegisterSourceOutput(generateProgram, static (spc, generate) =>
        {
            if (!generate)
            {
                return;
            }

            var (source, error) = GeneratorSafety.SafeEmit(EntryPointEmitter.Emit);
            if (error is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Descriptors.UnhandledException, Location.None, error));
                return;
            }

            spc.AddSource(EntryPointEmitter.HintName, SourceText.From(source!, Encoding.UTF8));
        });
```

(d) Replace the `Transform` method with a safety-wrapped version, and add the `MakeLocation` helper:

```csharp
    static ScenarioResult? Transform(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method || ctx.TargetNode is not MethodDeclarationSyntax syntax)
        {
            return null;
        }

        var lineSpan = syntax.Identifier.GetLocation().GetLineSpan();
        return GeneratorSafety.SafeParse(
            () => ScenarioParser.TryParse(ctx.SemanticModel, method, syntax),
            lineSpan.Path,
            lineSpan.StartLinePosition.Line + 1);
    }

    /// <summary>A 1-based file/line location for a diagnostic, or <see cref="Location.None"/> when the
    /// input had no path.</summary>
    static Location MakeLocation(string? file, int line)
    {
        if (string.IsNullOrEmpty(file) || line <= 0)
        {
            return Location.None;
        }

        var position = new LinePosition(line - 1, 0);
        return Location.Create(file!, new TextSpan(0, 0), new LinePositionSpan(position, position));
    }
```

- [ ] **Step 9: Build and run the generator suite (no regressions)**

Run: `dotnet build src\Raun.Generator\Raun.Generator.csproj`
Expected: succeeds with no RS2008 analyzer-release warnings (RAUN000 is now tracked).
Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj`
Expected: all PASS (existing lowering/snapshot/analyzer tests unaffected; the pipeline still emits identical output for valid input).

- [ ] **Step 10: Commit**

```bash
jj commit -m "feat(generator): report RAUN000 on unhandled parse/emit exceptions"
```

---

## Task 5: RAUN000 in the analyzer

**Files:**
- Modify: `src/Raun.Generator/Analysis/ScenarioAnalyzer.cs`
- Test: `test/Raun.Generator.Test/AnalyzerTests.cs`

- [ ] **Step 1: Write the failing test**

In `test/Raun.Generator.Test/AnalyzerTests.cs`, add:

```csharp
    [Fact]
    public void RAUN000_is_a_supported_diagnostic()
    {
        var analyzer = new Raun.Generator.Analysis.ScenarioAnalyzer();

        Assert.Contains(analyzer.SupportedDiagnostics, d => d.Id == "RAUN000");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj --filter "FullyQualifiedName~AnalyzerTests.RAUN000"`
Expected: FAIL — `RAUN000` is not in `SupportedDiagnostics`.

- [ ] **Step 3: Support RAUN000 and wrap per-method analysis**

In `src/Raun.Generator/Analysis/ScenarioAnalyzer.cs`:

(a) Add `using System;` and `using Raun.Generator;` to the using block.

(b) Add `Descriptors.UnhandledException,` as the first entry of the `SupportedDiagnostics` collection.

(c) Rename the existing `AnalyzeMethod` to `AnalyzeMethodCore`, and add a new wrapping `AnalyzeMethod`:

```csharp
    static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        try
        {
            AnalyzeMethodCore(context);
        }
        catch (Exception ex)
        {
            var location = ((MethodDeclarationSyntax)context.Node).Identifier.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.UnhandledException, location, GeneratorSafety.Describe(ex)));
        }
    }

    static void AnalyzeMethodCore(SyntaxNodeAnalysisContext context)
    {
        // ... unchanged body of the former AnalyzeMethod ...
    }
```

(The `RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration)` registration in `Initialize` is unchanged — it now points at the wrapper.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj --filter "FullyQualifiedName~AnalyzerTests"`
Expected: PASS (the new fact plus all existing RAUN00x facts — wrapping does not change their behavior).

- [ ] **Step 5: Commit**

```bash
jj commit -m "feat(generator): guard analyzer per-method analysis with RAUN000"
```

---

## Task 6: Sample — phase words in step names + class display name

**Files:**
- Modify: `samples/AppointmentTests/AppointmentDsl.cs`
- Modify: `samples/AppointmentTests/Scenarios.cs`

- [ ] **Step 1: Prefix each `[StepName]` with its phase word**

In `samples/AppointmentTests/AppointmentDsl.cs`, update the eight `[StepName]` templates so each starts with its phase word. Final values:

- `[StepName("Given the database is clean")]`
- `[StepName("Given patient {name} exists")]`
- `[StepName("Given an available slot exists")]`
- `[StepName("Given user {name} exists")]`
- `[StepName("When creating an appointment")]`
- `[StepName("When importing the users")]`
- `[StepName("Then the appointment should exist")]`
- `[StepName("Then the import should contain {expected} users")]`

- [ ] **Step 2: Name the scenario class**

In `samples/AppointmentTests/Scenarios.cs`, add `using System.ComponentModel;` to the using block, and put `[DisplayName("Appointment booking")]` on the `Scenarios` class:

```csharp
using Raun;
using System.ComponentModel;

namespace AppointmentTests;

/// <summary>
/// Scenarios authored as plain C#. The generator lowers each body into a graph the runtime runs,
/// reporting every Given/When/Then step as its own test. The test runner never executes these bodies
/// directly — they are source for the generator.
/// </summary>
[DisplayName("Appointment booking")]
public static class Scenarios
{
    // ... unchanged ...
}
```

- [ ] **Step 3: Build the sample**

Run: `dotnet build samples\AppointmentTests\AppointmentTests.csproj`
Expected: succeeds.

- [ ] **Step 4: List the discovered tree and eyeball it**

Run: `dotnet run --project samples\AppointmentTests\AppointmentTests.csproj -- --list-tests`
Expected: discovery succeeds; step leaves read `Given …` / `When …` / `Then …` (e.g. `1. Given patient Jane exists`, `3. When creating an appointment`), grouped under the `Appointment booking` class node.

- [ ] **Step 5: Run the sample's tests**

Run: `dotnet test samples\AppointmentTests\AppointmentTests.csproj`
Expected: all scenario step tests PASS.

- [ ] **Step 6: Commit**

```bash
jj commit -m "sample(appointments): phase words in step names + Appointment booking class node"
```

---

## Final verification

- [ ] **Whole-repo build:** `dotnet build` → succeeds, no analyzer-release (RS2008) warnings.
- [ ] **All test projects pass:**
  - `dotnet test test\Raun.Test\Raun.Test.csproj`
  - `dotnet test test\Raun.Generator.Test\Raun.Generator.Test.csproj`
  - `dotnet test test\Raun.Mtp.Test\Raun.Mtp.Test.csproj`
  - `dotnet test samples\AppointmentTests\AppointmentTests.csproj`
- [ ] **Spec coverage:** Feature 1 (Task 6), Feature 2 (Tasks 2–3, 6), Feature 3 (Task 1), Feature 4 (Tasks 4–5) all implemented.
