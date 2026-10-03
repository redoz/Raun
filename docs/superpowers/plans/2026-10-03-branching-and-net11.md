# Pattern Branching and .NET 11 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scenarios branch on what a step returned — `if (await S is P)` and
`switch (await S) { … }` — on a runtime model where a condition node selects one of N arms, with
Raun moved to `net11.0` / C# 15 so union and closed-hierarchy results switch exhaustively.

**Architecture:**
- A condition node carries `SelectArm: Func<IStepInputs, int>` and `Arms: string[]`; a guard is
  `Guard(ConditionIndex, Arm)`. `if`/`else` is the two-arm case.
- The generator emits `SelectArm` as the real C# `switch`/`is` over the recorded result, so pattern
  semantics belong to the compiler.
- Pattern variables come from hidden per-arm *case nodes*. Locals merge N-way after a branch.
- The parser stays the only reader of scenario bodies (one-walker design).

**Tech Stack:** C# 15 / .NET 11 RC1 SDK, Roslyn incremental generator (netstandard2.0),
Microsoft.Testing.Platform, xUnit v3 tests, jj for version control.

**Spec:** `docs/superpowers/specs/2026-10-03-branching-and-net11-design.md`. Two refinements below
are recorded in the spec in Task 7.

### Spec refinements decided while planning

1. **`SelectArm` is `Func<IStepInputs, int>`, not `Func<object?, int>`.** A `when` clause may read
   *other* step outputs, so the selector needs the inputs. It reads the condition's own value through
   `IStepInputs.Get<T>(conditionIndex)`, the same way `Invoke` and `FormatDisplayName` already work.
2. **The shipped generator stays on its current Roslyn baseline. Only the test projects take the RC
   compiler package.** Branching uses no new Roslyn API: the generator copies patterns into generated
   code, and the consumer's compiler binds them. Building the shipped generator against an RC package
   would put a prerelease dependency into the package for no gain. The tests still need the C# 15
   parser to compile union sources, so they reference it.

## Global Constraints

- Version control is **jj**. All mutations go through `jj`:
  - `jj describe -m`, `jj new`, `jj commit -m`;
  - `git` only for read-only commands and for tags, as RELEASING.md describes.
- Work in the jj workspace `C:\dev\raun-issue2`. Shell `cd` does not persist, so use
  `cd /c/dev/raun-issue2 && …` in every command.
- Commit messages: plain subject and body, **no `Co-Authored-By` or tooling trailers**.
- Line endings: **LF**. Files written on Windows can come out as CRLF. Before every commit run:
  ```
  python3 -c "import subprocess,os;r='C:/dev/raun-issue2';[open(os.path.join(r,n),'wb').write(open(os.path.join(r,n),'rb').read().replace(b'\r\n',b'\n')) for n in subprocess.run(['jj','-R',r,'diff','--name-only'],capture_output=True,text=True).stdout.split() if os.path.isfile(os.path.join(r,n))]"
  ```
- Build gate: `dotnet build Raun.slnx -c Release` must give **0 warnings**. Ignore `MINVER1001`,
  which this jj workspace causes because it has no `.git`. `dotnet test Raun.slnx` must be green.
- **The generator never parses text.** All emitted code is built with `SyntaxFactory`, or is reused
  in-tree syntax; the `EmissionDisciplineTests` guard enforces this. Method names must not start with
  `Parse` when they are called with a receiver.
- **Every scenario-body rule lives in `ScenarioParser`.** It reports through `Report(...)` and keeps
  walking. The analyzer gets no scenario-body rules.
- **Snapshots** (`test/Raun.Generator.Test/Snapshots`):
  - Accept with `RAUN_ACCEPT_SNAPSHOTS=1` *only* where a task says a snapshot moves on purpose, and
    review the diff.
  - Any other snapshot that moves is a bug.
- **TFM: `net11.0` only.** The SDK floor is `11.0.100`; MSBuild version comparison ignores the
  prerelease suffix.
- **Libraries stay on their stable 10.x versions.** `Microsoft.Extensions.*` and Aspire are not moved
  to RC packages.
- **New authoring shapes need a sample.** Each one gets a `SampleSources` `*Scenario` constant, listed
  in `LoweringCoverageTests.DslFor`.

## Review Focus

These are the five failure modes most likely to bite a user that no single test would naturally pin
down. Each one has a test in the task named.

1. **A `when` clause throws at run time,** for example by reading `x.Items[3]` on a short list. The
   condition step should be reported **Failed** with that exception, not crash the scheduler or
   silently take no arm. (Task 2)
2. **A `switch` where no arm matches and there is no `default`.** Every arm step should be **not
   taken**, with the reason `matched no arm`. The steps after the switch should still run. (Task 5)
3. **A pattern variable used in a display-name placeholder,** e.g. `[StepName("notify {reason}")]`
   with `rejected.Reason`. The name should render the live value. (Task 4)
4. **A local assigned in some arms of a switch, but not all, then read after the switch, where the
   switch has a `default`.** The arms that left it alone should pass the earlier value through, and
   the reader should see the value from whichever arm ran. (Task 5)
5. **A union case that is a value type** (e.g. `union Count(int, string)`). It should match through
   the union and bind the unboxed value. (Task 6)

---

### Task 0: Install the .NET 11 RC1 SDK

**Files:** none in the repo.

- [ ] **Step 1: Check whether it is already installed**

Run: `dotnet --list-sdks`
Expected: a line starting with `11.0.100-rc.1`. If it is there, skip to Step 3.

- [ ] **Step 2: Install it (Patrik approved, 2026-10-03)**

Run (PowerShell; this raises a UAC prompt that Patrik accepts):

```powershell
winget install --id Microsoft.DotNet.SDK.Preview --exact --accept-source-agreements --accept-package-agreements
```

If winget is unavailable, download the installer from https://dotnet.microsoft.com/download/dotnet/11.0
(SDK `11.0.100-rc.1.26425.128`, Windows x64) and run it.

- [ ] **Step 3: Verify**

Run: `dotnet --list-sdks`
Expected: both `10.0.401` and `11.0.100-rc.1.26425.128`.

Then record the RC compiler version, which Task 1 needs:

```bash
powershell -c "(Get-Item 'C:\Program Files\dotnet\sdk\11.0.100-rc.1.26425.128\Roslyn\bincore\Microsoft.CodeAnalysis.CSharp.dll').VersionInfo.ProductVersion"
```

Expected: a value like `5.12.0-2.26421.7+<sha>`. Keep the part before `+`; it is
`<RC_ROSLYN>` in Task 1.

---

### Task 1: Move the platform to .NET 11

**Files:**
- Create: `global.json`
- Modify: `nuget.config`, `Directory.Packages.props`, `Directory.Build.props:10`
- Modify: every `<TargetFramework>net10.0</TargetFramework>` under `src/`, `test/` and `samples/`.
  Find them with `grep -rln "net10.0" --include=*.csproj .`
- Modify: `src/Raun/buildTransitive/Raun.props:20-34`
- Modify: `test/Raun.Mtp.Test/SdkFloorTests.cs:19-41`
- Modify: `.github/workflows/ci.yml:21-23`, `.github/workflows/release.yml` (the `setup-dotnet` step)
- Modify: `README.md:29-31`
- Modify: `test/Raun.Generator.Test/Raun.Generator.Test.csproj`
- Regenerate: every `packages.lock.json` and `packages.*.lock.json`

**Interfaces:**
- Produces: a repo that builds and tests on `net11.0`. `Microsoft.CodeAnalysis.CSharp <RC_ROSLYN>`
  is available to test projects as package version `RaunTestRoslynVersion`.

- [ ] **Step 1: Write the failing floor tests**

In `test/Raun.Mtp.Test/SdkFloorTests.cs`, replace the two theory data lists:

```csharp
    [Theory]
    [InlineData("9.0.100")]
    [InlineData("10.0.100")]
    [InlineData("10.0.401")]
    public async Task An_sdk_below_the_floor_fails_the_build(string sdkVersion)
```

```csharp
    [Theory]
    [InlineData("11.0.100")]
    [InlineData("11.0.100-rc.1.26425.128")]   // MSBuild version comparison ignores the suffix
    [InlineData("11.0.100-preview.7.26380.1")]
    [InlineData("11.0.200")]
    [InlineData("12.0.100")]
    public async Task An_sdk_at_or_above_the_floor_builds(string sdkVersion)
```

- [ ] **Step 2: Run them to see them fail**

Run: `cd /c/dev/raun-issue2 && dotnet test test/Raun.Mtp.Test --filter-class "*SdkFloorTests"`
Expected: FAIL. `10.0.401` currently builds.

- [ ] **Step 3: Raise the floor**

In `src/Raun/buildTransitive/Raun.props`, set `<RaunMinimumSdkVersion>11.0.100</RaunMinimumSdkVersion>`
and replace the comment above it:

```xml
    <!-- The SDK a Raun consumer needs: .NET 11 (C# 15). Raun targets net11.0, and an older compiler
         cannot load Raun's source generator at all: no scenarios, no diagnostics, a green run of zero
         tests. That is far worse than a build error, hence the check below. MSBuild's version
         comparison ignores a prerelease suffix, so every 11.0.100 preview and RC passes it. -->
```

In the `RaunCheckSdkFloor` target's error text, change `Raun requires the .NET SDK
$(RaunMinimumSdkVersion) or later` to `Raun requires the .NET 11 SDK ($(RaunMinimumSdkVersion) or
later)`.

- [ ] **Step 4: Pin the SDK, retarget, and add the RC compiler for tests**

Create `global.json`:

```json
{
  "sdk": {
    "version": "11.0.100-rc.1.26425.128",
    "allowPrerelease": true,
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

If a `global.json` already exists (it holds the `test.runner` block), merge the `sdk` block into it.

Retarget every project:

```bash
cd /c/dev/raun-issue2 && grep -rln "<TargetFramework>net10.0</TargetFramework>" --include=*.csproj . | grep -v /obj/ | xargs sed -i 's#<TargetFramework>net10.0</TargetFramework>#<TargetFramework>net11.0</TargetFramework>#'
```

Change the comment in `Directory.Build.props:10` to `<!-- C# 15 (net11.0 default): unions, closed hierarchies -->`.

Replace `nuget.config`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <!-- .NET 11 RC1's compiler packages are not on nuget.org yet. This feed serves ONLY them (see
         the mapping below), and only the test projects use them. Remove at .NET 11 GA. -->
    <add key="dotnet-tools" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
    <packageSource key="dotnet-tools">
      <package pattern="Microsoft.CodeAnalysis.CSharp" />
      <package pattern="Microsoft.CodeAnalysis.Common" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

NuGet's source mapping picks the most specific pattern, so these two packages resolve from
`dotnet-tools` and everything else from nuget.org. If restore cannot find `5.9.0` there for the
generator project, add `<package pattern="Microsoft.CodeAnalysis.*" />` to `nuget.org` and pin the RC
version in the test project only. That is Step 4b.

In `Directory.Packages.props`, add next to the existing CodeAnalysis entry:

```xml
    <!-- The C# 15 parser for the generator TESTS (union and closed-hierarchy sources). The shipped
         generator keeps its baseline above: it calls no new Roslyn API. Move to the GA package at
         .NET 11 GA. -->
    <RaunTestRoslynVersion>RC_ROSLYN</RaunTestRoslynVersion>
```

Put this inside a `<PropertyGroup>`, with `RC_ROSLYN` replaced by the value from Task 0 Step 3. In
`test/Raun.Generator.Test/Raun.Generator.Test.csproj`, find the `Microsoft.CodeAnalysis.CSharp`
reference (directly or via `Microsoft.CodeAnalysis.CSharp.Workspaces`) and add
`VersionOverride="$(RaunTestRoslynVersion)"`. If the test project references the generator project,
also add an explicit `<PackageReference Include="Microsoft.CodeAnalysis.CSharp" VersionOverride="$(RaunTestRoslynVersion)" />`
so the higher version wins in the test process.

- [ ] **Step 5: Update CI and the README**

In both workflow files, change the `setup-dotnet` `with:` block to:

```yaml
        with:
          dotnet-version: 11.0.x
          dotnet-quality: preview
```

In `README.md`, replace the first paragraph of the "Install" section:

```markdown
**Requires the .NET 11 SDK (11.0.100 or later; the RC works).** Raun targets `net11.0`, and its DSL
and branching use C# 15. An older compiler would load no generator at all — no scenarios, no
diagnostics, a green run of zero tests — so Raun fails such a build with `RAUN018` instead.
```

- [ ] **Step 6: Regenerate the lock files and run everything**

Run: `cd /c/dev/raun-issue2 && dotnet restore Raun.slnx && dotnet restore Raun.slnx --locked-mode`
Expected: both succeed. The second proves the lock files are current.

Run: `cd /c/dev/raun-issue2 && dotnet build Raun.slnx -c Release 2>&1 | grep -E "warning|error" | grep -v MINVER`
Expected: no output.

Run: `cd /c/dev/raun-issue2 && dotnet test Raun.slnx`
Expected: all green, the new floor cases included. Any failure here is a .NET 11 behaviour change.
Fix it in this task; do not suppress it.

- [ ] **Step 7: Commit**

```bash
cd /c/dev/raun-issue2 && jj describe -m "build!: move to .NET 11 (net11.0, C# 15) on the RC1 SDK

Every project targets net11.0; global.json pins the RC1 SDK; the SDK floor
(RAUN018) is 11.0.100. The test projects take RC1's compiler package from
the dotnet-tools feed, source-mapped to Microsoft.CodeAnalysis only, so
union and closed-hierarchy sources compile in generator tests; the shipped
generator keeps its baseline, since branching needs no new Roslyn API.
Remove the feed at .NET 11 GA." && jj new
```

---

### Task 2: Runtime model — a condition selects an arm

**Files:**
- Modify: `src/Raun/Model/Guard.cs`
- Modify: `src/Raun/Model/ScenarioNode.cs:78-85`
- Modify: `src/Raun/Model/ScenarioDefinition.cs:106-176`
- Modify: `src/Raun/Scheduling/ScenarioScheduler.cs` (guard checks near lines 218-243 and 309-313,
  `ApplyFinishedAsync` near line 569, `EvaluateGuard` near line 763)
- Modify: `src/Raun.Generator/Lowering/Ir.cs` (`ParsedGuard`, `ParsedStep.ConditionCoercionType`)
- Modify: `src/Raun.Generator/Lowering/ScenarioParser.cs` (`ParsedGuard` construction, `MarkAsCondition`)
- Modify: `src/Raun.Generator/Emit/ScenarioEmitter.cs:311-326, 486-497`
- Modify tests:
  - `test/Raun.Test/SchedulerTests.cs`
  - `test/Raun.Test/ModelTests.cs`
  - `test/Raun.Test/TeardownTests.cs`
  - `test/Raun.Test/Resources/ResourceLedgerTests.cs`
  - `test/Raun.Test/Running/RunLoopTests.cs`
  - `test/Raun.Mtp.Test/RunLoopFrameworkTests.cs`
  - `test/Raun.Generator.Test/ConditionalLoweringTests.cs`
- Snapshot that moves **on purpose**: `GeneratorSnapshotTests.Conditional_scenario#…Routing.g.verified.cs`

**Interfaces:**
- Produces:
  - **`Raun.Model.Guard`:** `public readonly record struct Guard(int ConditionIndex, int Arm)`.
  - **`ScenarioNode` gains:**
    - `public Func<IStepInputs, int>? SelectArm { get; init; }`
    - `public IReadOnlyList<string> Arms { get; init; } = [];`

    `EvaluateCondition` is removed.
  - **Generator IR:**
    - `internal readonly record struct ParsedGuard(int ConditionIndex, int Arm);`
    - `ParsedStep.Branch : ParsedBranch?`, which replaces `ConditionCoercionType`.
  - **`ParsedBranch`** (new, in `Ir.cs`). Task 2 uses only `Truth`; Tasks 4 and 5 add the other forms.

    ```csharp
    internal enum BranchForm { Truth, Pattern, Switch }
    internal sealed record ParsedBranch
    {
        public BranchForm Form { get; init; }
        public TypeSyntax ValueType { get; init; }                      // the condition step's T
        public PatternSyntax? Pattern { get; init; }                    // Pattern: arm 0 iff value is Pattern
        public IReadOnlyList<SwitchSectionSyntax> Sections { get; init; } // Switch: one per arm, labels only
        public IReadOnlyList<string> ArmLabels { get; init; }
    }
    ```

- [ ] **Step 1: Write the failing runtime tests**

In `test/Raun.Test/SchedulerTests.cs`:

1. Replace the `Cond` and `ThrowingCond` helpers with:

```csharp
    private static ScenarioNode Cond(int index, bool value, int[]? dependsOn = null, Guard[]? guards = null) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Cond{index}",
        DisplayNameTemplate = $"cond {index}",
        DependsOn = dependsOn ?? [],
        Guards = guards ?? [],
        Invoke = (_, _) => Task.FromResult<object?>(value),
        SelectArm = inputs => inputs.Get<bool>(index) ? 0 : 1,
        Arms = ["if", "else"],
    };

    private static ScenarioNode ThrowingCond(int index, int[]? dependsOn = null) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "Given",
        OperationName = $"Cond{index}",
        DisplayNameTemplate = $"cond {index}",
        DependsOn = dependsOn ?? [],
        Invoke = (_, _) => throw new InvalidOperationException("condition failed"),
        SelectArm = inputs => inputs.Get<bool>(index) ? 0 : 1,
        Arms = ["if", "else"],
    };

    /// <summary>A condition producing <paramref name="value"/> that selects through
    /// <paramref name="select"/> among <paramref name="arms"/>.</summary>
    private static ScenarioNode Select(int index, object? value, Func<IStepInputs, int> select, params string[] arms) => new()
    {
        Index = index,
        StepId = $"step-{index}",
        Phase = "When",
        OperationName = $"Choose{index}",
        DisplayNameTemplate = $"choose {index}",
        DependsOn = [],
        Invoke = (_, _) => Task.FromResult(value),
        SelectArm = select,
        Arms = arms,
    };
```

2. Change every guard in the file: `new Guard(x, true)` becomes `new Guard(x, 0)`, and
   `new Guard(x, false)` becomes `new Guard(x, 1)`.

```bash
cd /c/dev/raun-issue2 && sed -i -E 's/new Guard\(([0-9]+), true\)/new Guard(\1, 0)/g; s/new Guard\(([0-9]+), false\)/new Guard(\1, 1)/g' test/Raun.Test/*.cs test/Raun.Test/*/*.cs test/Raun.Mtp.Test/*.cs test/Raun.Generator.Test/*.cs
```

3. Add these tests:

```csharp
    [Fact]
    public async Task A_condition_selects_exactly_one_of_n_arms()
    {
        var ran = new bool[3];
        var def = Def(
            Select(0, "b", inputs => inputs.Get<string>(0) switch { "a" => 0, "b" => 1, _ => 2 },
                "case \"a\"", "case \"b\"", "default"),
            Arm(1, [new Guard(0, 0)], (_, _) => { ran[0] = true; return Task.FromResult<object?>(null); }, 0),
            Arm(2, [new Guard(0, 1)], (_, _) => { ran[1] = true; return Task.FromResult<object?>(null); }, 0),
            Arm(3, [new Guard(0, 2)], (_, _) => { ran[2] = true; return Task.FromResult<object?>(null); }, 0));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.Equal([false, true, false], ran);
        Assert.Equal(StepStatus.NotTaken, results[1].Status);
        Assert.Equal("not taken: Choose0 took case \"b\"", results[1].SkipReason);
        Assert.Equal(StepStatus.Passed, results[2].Status);
    }

    [Fact]
    public async Task No_matching_arm_leaves_every_arm_not_taken()
    {
        var def = Def(
            Select(0, 7, _ => -1, "case 1", "case 2"),
            Arm(1, [new Guard(0, 0)], Pass(), 0),
            Arm(2, [new Guard(0, 1)], Pass(), 0));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.All(results.Skip(1), r => Assert.Equal(StepStatus.NotTaken, r.Status));
        Assert.Equal("not taken: Choose0 matched no arm", results[1].SkipReason);
    }

    [Fact]
    public async Task A_throwing_arm_selection_fails_the_condition_step()
    {
        var def = Def(
            Select(0, "x", _ => throw new IndexOutOfRangeException("when clause"), "case A", "default"),
            Arm(1, [new Guard(0, 0)], Pass(), 0),
            Arm(2, [new Guard(0, 1)], Pass(), 0));

        var results = await new ScenarioScheduler().RunAsync(def);

        Assert.Equal(StepStatus.Failed, results[0].Status);
        Assert.IsType<IndexOutOfRangeException>(results[0].Exception?.InnerException);
        Assert.All(results.Skip(1), r => Assert.Equal(StepStatus.Skipped, r.Status));
    }

    [Fact]
    public async Task Arm_selection_runs_once_per_condition()
    {
        var calls = 0;
        var def = Def(
            Select(0, 1, _ => { calls++; return 0; }, "case 1", "default"),
            Arm(1, [new Guard(0, 0)], Pass(), 0),
            Arm(2, [new Guard(0, 0)], Pass(), 0),
            Arm(3, [new Guard(0, 1)], Pass(), 0));

        await new ScenarioScheduler().RunAsync(def);

        Assert.Equal(1, calls);
    }
```

In `test/Raun.Test/ModelTests.cs`, change the `Cond` helper's last member to:

```csharp
        SelectArm = inputs => inputs.Get<bool>(index) ? 0 : 1,
        Arms = ["if", "else"],
```

Then add:

```csharp
    [Fact]
    public void Validate_rejects_a_guard_on_an_arm_the_condition_does_not_have()
    {
        var def = Def(Cond(0), Guarded(1, [new Guard(0, 2)], 0));

        var error = Assert.Throws<InvalidOperationException>(def.Validate);
        Assert.Contains("arm 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_accepts_an_n_way_merge_over_distinct_arms_of_one_condition()
    {
        var three = new ScenarioNode
        {
            Index = 0, StepId = "c", Phase = "When", OperationName = "C", DisplayNameTemplate = "c",
            DependsOn = [], Invoke = (_, _) => Task.FromResult<object?>(0),
            SelectArm = _ => 0, Arms = ["case 0", "case 1", "default"],
        };
        var def = Def(three, Guarded(1, [new Guard(0, 0)], 0), Guarded(2, [new Guard(0, 1)], 0),
            Guarded(3, [new Guard(0, 2)], 0), Merge(4, 1, 2, 3));

        def.Validate();
    }
```

(If `ModelTests` has no `Def` helper, inline `new ScenarioDefinition { ScenarioId = "s", DisplayName = "s", MethodName = "N.S", Nodes = [...] }`.)

In `test/Raun.Mtp.Test/RunLoopFrameworkTests.cs`, the node helper takes
`Func<object?, bool>? evaluate`. Replace its `EvaluateCondition = evaluate,` line with:

```csharp
            SelectArm = evaluate is null ? null : inputs => evaluate(inputs.Get<object?>(index)) ? 0 : 1,
            Arms = evaluate is null ? [] : ["if", "else"],
```

(`index` is the helper's own first parameter.)

In `test/Raun.Generator.Test/ConditionalLoweringTests.cs`, change
`Assert.NotNull(def.Nodes[2].EvaluateCondition);` to:

```csharp
        Assert.NotNull(def.Nodes[2].SelectArm);
        Assert.Equal(["if", "else"], def.Nodes[2].Arms);
```

- [ ] **Step 2: Run them to see them fail**

Run: `cd /c/dev/raun-issue2 && dotnet build Raun.slnx 2>&1 | grep -E " error " | head`
Expected: compile errors: `Guard` has no `int` second parameter, and `SelectArm` and `Arms` do not
exist.

- [ ] **Step 3: Change the model**

`src/Raun/Model/Guard.cs`:

```csharp
namespace Raun.Model;

/// <summary>
/// A branch a node is gated on: the node runs only when the condition node at
/// <see cref="ConditionIndex"/> passed AND selected <see cref="Arm"/> (its
/// <see cref="ScenarioNode.SelectArm"/>; for an <c>if</c>, arm 0 is the body and arm 1 the
/// <c>else</c>). Nested branches stack guards; all of them must hold.
/// </summary>
public readonly record struct Guard(int ConditionIndex, int Arm);
```

In `src/Raun/Model/ScenarioNode.cs`, replace the `EvaluateCondition` property and its doc comment with:

```csharp
    /// <summary>
    /// For a condition node: which arm runs. Called once, after the node passes, with the scenario's
    /// inputs (the node's own output is <c>inputs.Get&lt;T&gt;(Index)</c>, and a <c>when</c> clause
    /// may read other outputs it depends on). Returns an index into <see cref="Arms"/>, or -1 when
    /// no arm matches. The generator emits the scenario's own <c>switch</c> or <c>is</c> here, so
    /// pattern semantics are the compiler's. Null for a node that gates nothing.
    /// </summary>
    public Func<IStepInputs, int>? SelectArm { get; init; }

    /// <summary>For a condition node, each arm's label as the source writes it — <c>if</c>/<c>else</c>,
    /// <c>case Rejected rejected</c>, <c>default</c>. Guards index into it; the not-taken reason
    /// quotes it.</summary>
    public IReadOnlyList<string> Arms { get; init; } = [];
```

In `src/Raun/Model/ScenarioDefinition.cs`, replace the `EvaluateCondition is null` check inside
`foreach (var guard in node.Guards)` with:

```csharp
                var condition = Nodes[guard.ConditionIndex];
                if (condition.SelectArm is null)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') is guarded on step {guard.ConditionIndex} "
                        + $"('{condition.OperationName}'), which has no SelectArm.");
                }

                if (guard.Arm < 0 || guard.Arm >= condition.Arms.Count)
                {
                    throw new InvalidOperationException(
                        $"Step {node.Index} ('{node.OperationName}') is guarded on arm {guard.Arm} of step "
                        + $"{guard.ConditionIndex} ('{condition.OperationName}'), which has {condition.Arms.Count} arm(s).");
                }
```

Replace the exclusivity comment and `AreExclusive`:

```csharp
            // Merge sources must be mutually exclusive — every pair must be guarded on a common
            // condition, on different arms — so at most one can pass. The generator guarantees this;
            // without the check a violation would surface as a baffling double-write.
```

```csharp
    /// <summary>True when two candidate producers can never both run: some condition guards both,
    /// on different arms.</summary>
    private static bool AreExclusive(ScenarioNode left, ScenarioNode right)
    {
        foreach (var l in left.Guards)
        {
            foreach (var r in right.Guards)
            {
                if (l.ConditionIndex == r.ConditionIndex && l.Arm != r.Arm)
                {
                    return true;
                }
            }
        }

        return false;
    }
```

In the same file, update the exception text `(no shared condition with opposite guard values)` to
`(no shared condition guarding them on different arms)`.

- [ ] **Step 4: Change the scheduler**

In `src/Raun/Scheduling/ScenarioScheduler.cs`:

1. Next to the `outputs`/`status` arrays (they sit near `var inputs = new StepInputs(outputs, status);`),
   add:

```csharp
        // The arm each passed condition node selected, decided once when it passed; -1 = no arm.
        var selectedArm = new int[nodes.Count];
```

2. In `ApplyFinishedAsync`, replace the `if (outcome.Result.Status == StepStatus.Passed) { outputs[i] = outcome.Output; }`
   block, and the line before it, with:

```csharp
            var result = outcome.Result;
            status[i] = result.Status;
            if (result.Status == StepStatus.Passed)
            {
                outputs[i] = outcome.Output;
                if (nodes[i].SelectArm is { } select)
                {
                    // The branch decision is part of the condition step: a when-clause that throws
                    // fails the step that holds it, never the scheduler, and its arms cascade as for
                    // any failed dependency.
                    try
                    {
                        selectedArm[i] = select(inputs);
                    }
                    catch (Exception ex)
                    {
                        status[i] = StepStatus.Failed;
                        result = result with
                        {
                            Status = StepStatus.Failed,
                            Exception = new InvalidOperationException(
                                $"Choosing a branch after '{nodes[i].OperationName}' threw.", ex),
                        };
                    }
                }
            }
```

   Then use `result` in place of `outcome.Result` for the rest of the method (`results[i] = result;`,
   and `OnStepFinishedAsync(result)`). If `StepResult` is not a `record`, build the failed result
   with an object initializer that copies the fields `ApplyFinishedAsync` uses (`Node`,
   `DisplayName`, `StartedAt`, `Duration`, `Logs`, `LogEntries`, `Attachments`, `Effects`, `Lineage`,
   `TraceId`, `SpanId`).

3. In phase 1b, replace the `case StepStatus.Passed:` body with:

```csharp
                        case StepStatus.Passed:
                            if (selectedArm[guard.ConditionIndex] != guard.Arm)
                            {
                                guardNotTaken ??= NotTakenReason(nodes[guard.ConditionIndex], selectedArm[guard.ConditionIndex]);
                            }

                            break;
```

   Change the NotTaken application below it to
   `await ApplyTerminalAsync(i, StepStatus.NotTaken, guardNotTaken).ConfigureAwait(false);`, which
   drops the `$"not taken: {…}"` wrapper because `NotTakenReason` builds the full text.

4. In phase 2's readiness check, replace the guard clause with:

```csharp
                        && node.Guards.All(g => status[g.ConditionIndex] == StepStatus.Passed
                            && selectedArm[g.ConditionIndex] == g.Arm))
```

5. Replace `EvaluateGuard` with:

```csharp
    /// <summary>Why a guarded step did not run: the arm its condition took instead, or that none
    /// matched.</summary>
    private static string NotTakenReason(ScenarioNode condition, int arm)
        => arm < 0
            ? $"not taken: {condition.OperationName} matched no arm"
            : $"not taken: {condition.OperationName} took {condition.Arms[arm]}";
```

   Update any existing scheduler test that asserts the old reason text `not taken: Cond0`. The new
   text for an if-condition that was false is `not taken: Cond0 took else`.

- [ ] **Step 5: Convert the generator's IR and emitter**

In `src/Raun.Generator/Lowering/Ir.cs`:

```csharp
/// <summary>A lowered branch guard: the node runs only when node <see cref="ConditionIndex"/> passed
/// and selected <see cref="Arm"/>. Mirrors <c>Raun.Model.Guard</c>.</summary>
internal readonly record struct ParsedGuard(int ConditionIndex, int Arm);

/// <summary>How a branch's condition node is written.</summary>
internal enum BranchForm
{
    /// <summary><c>if (await S)</c>: arm 0 when the value is true, arm 1 otherwise.</summary>
    Truth,

    /// <summary><c>if (await S is P)</c>: arm 0 when the value matches <see cref="ParsedBranch.Pattern"/>.</summary>
    Pattern,

    /// <summary><c>switch (await S)</c>: arm k is section k.</summary>
    Switch,
}

/// <summary>
/// A condition node's branch, lowered: what the emitter turns into <c>ScenarioNode.SelectArm</c> and
/// <c>Arms</c>. Patterns and labels are already re-hosted (names qualified, step outputs read through
/// <c>__inputs</c>).
/// </summary>
internal sealed record ParsedBranch
{
    private readonly Syn<TypeSyntax> _valueType;
    private readonly Syn<PatternSyntax> _pattern;
    private readonly EquatableArray<Syn<SwitchSectionSyntax>> _sections;
    private readonly EquatableArray<string> _armLabels;

    public BranchForm Form { get; init; }

    /// <summary>The condition step's result type: what <c>__inputs.Get&lt;T&gt;</c> reads.</summary>
    public TypeSyntax ValueType { get => _valueType.Node!; init => _valueType = value; }

    public PatternSyntax? Pattern { get => _pattern.Node; init => _pattern = value; }

    /// <summary>For <see cref="BranchForm.Switch"/>: one section per arm, labels only (no statements).</summary>
    public IReadOnlyList<SwitchSectionSyntax> Sections
    {
        get => new SynList<SwitchSectionSyntax>(_sections);
        init => _sections = SynList<SwitchSectionSyntax>.Wrap(value);
    }

    public IReadOnlyList<string> ArmLabels
    {
        get => _armLabels;
        init => _armLabels = Equatable.Of(value);
    }
}
```

(`Syn<T>`, `SynList<T>` and `Equatable.Of` already exist in `Lowering/Equatable.cs`. Mirror how
`ParsedScenario.Usings` uses `SynList`.)

In `ParsedStep`, replace the `_conditionCoercionType` field and the `ConditionCoercionType` property with:

```csharp
    /// <summary>When this step is a branch's condition: how it selects an arm. Null otherwise.</summary>
    public ParsedBranch? Branch { get; init; }
```

In `ScenarioParser.cs`:
- `WalkArm` takes `int arm` in place of `bool whenValue`, and pushes `new ParsedGuard(conditionIndex, arm)`.
- `ParseIf` passes `arm: 0` for the body and `arm: 1` for the else.
- `InsertMerge`'s `Side(...)` passes `arm: 0` / `arm: 1`.
- `InsertPassThrough(string name, int conditionIndex, int arm, int parentDef)` pushes
  `new(conditionIndex, arm)`.
- `MarkAsCondition` becomes:

```csharp
    private void MarkAsCondition(ParsedStep condition, ParsedBranch branch)
    {
        var position = _steps.FindIndex(s => s.Index == condition.Index);
        _steps[position] = _steps[position] with { Branch = branch };
    }
```

  `ParseCondition` calls it with:

```csharp
        MarkAsCondition(step, new ParsedBranch
        {
            Form = BranchForm.Truth,
            ValueType = step.ResultType,
            ArmLabels = ["if", "else"],
        });
```

In `ScenarioEmitter.cs`, replace the `ConditionCoercionType` block with:

```csharp
        if (step.Branch is { } branch)
        {
            members.Add(Set("SelectArm", SelectArmLambda(step.Index, branch)));
            members.Add(Set("Arms", ArrayOf(
                PredefinedType(Token(SyntaxKind.StringKeyword)),
                branch.ArmLabels.Select(label => (ExpressionSyntax)Lit(label)))));
        }
```

and add:

```csharp
    /// <summary>
    /// <c>static __inputs => …</c> choosing the arm from the condition's recorded value, read with
    /// <c>__inputs.Get&lt;T&gt;(index)</c>. Truth: <c>(__v) ? 0 : 1</c>, so Roslyn picks bool, an
    /// implicit conversion, or <c>operator true</c> at compile time; the scheduler never reflects.
    /// </summary>
    private static SimpleLambdaExpressionSyntax SelectArmLambda(int conditionIndex, ParsedBranch branch)
    {
        var value = InputsGet(branch.ValueType, conditionIndex);
        ExpressionSyntax body = branch.Form switch
        {
            BranchForm.Truth => ConditionalExpression(ParenthesizedExpression(value), Num(0), Num(1)),
            _ => throw new System.NotSupportedException(branch.Form.ToString()),
        };

        return SimpleLambdaExpression(Parameter(Identifier("__inputs")))
            .WithModifiers(TokenList(Token(SyntaxKind.StaticKeyword)))
            .WithExpressionBody(body);
    }

    /// <summary><c>__inputs.Get&lt;type&gt;(index)</c>.</summary>
    private static InvocationExpressionSyntax InputsGet(TypeSyntax type, int index)
        => InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("__inputs"),
                    GenericName(Identifier("Get"))
                        .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList(type)))))
            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(Num(index)))));
```

In `GuardArray`, replace the boolean literal argument with `Argument(Num(g.Arm))`, and fix its doc
comment to `new(i, arm)`.

- [ ] **Step 6: Run everything; accept the one snapshot that moves on purpose**

Run: `cd /c/dev/raun-issue2 && dotnet test Raun.slnx 2>&1 | grep -E "^failed|failed:|succeeded:"`
Expected: only `GeneratorSnapshotTests.Conditional_scenario` fails.

Accept it and review the diff:

```bash
cd /c/dev/raun-issue2 && RAUN_ACCEPT_SNAPSHOTS=1 dotnet test test/Raun.Generator.Test --filter-method "*Conditional_scenario*" && jj diff test/Raun.Generator.Test/Snapshots
```

Expected diff:
- the `EvaluateCondition = static __o => ((bool)__o!) ? true : false,` line becomes
  `SelectArm = static __inputs => (__inputs.Get<bool>(N)) ? 0 : 1,` plus
  `Arms = new string[] { "if", "else" },`;
- guards change from `new(…, true)`/`new(…, false)` to `new(…, 0)`/`new(…, 1)`.

Nothing else may change.

Run: `cd /c/dev/raun-issue2 && dotnet test Raun.slnx 2>&1 | grep -E "^failed|failed:|succeeded:"`
Expected: `failed: 0`.

- [ ] **Step 7: Commit**

```bash
cd /c/dev/raun-issue2 && jj describe -m "feat!: a condition node selects one of N arms

Guard(ConditionIndex, Arm) replaces Guard(ConditionIndex, WhenValue) and
ScenarioNode.SelectArm(IStepInputs) -> int replaces EvaluateCondition, with
Arms labelling each arm; if/else is the two-arm case (arm 0 the body, arm 1
the else). The scheduler selects once when a condition passes; a selector
that throws fails the condition step. A not-taken step says which arm ran
('took else', 'took case Rejected rejected') or that none matched. Merge
sources are exclusive when guarded on different arms of one condition." && jj new
```

---

### Task 3: N-arm branches in the parser

Generalizes the two-arm walk, rejoin and merge to N arms, preserving behaviour. Tasks 4 and 5 then
only add new *forms*.

**Files:**
- Modify: `src/Raun.Generator/Lowering/ScenarioParser.cs` (`ParseIf`, `WalkArm`, `DifferingLocals`,
  `InsertMerge`, `InsertPassThrough`)
- Test: `test/Raun.Generator.Test/ConditionalLoweringTests.cs`

**Interfaces:**
- Produces (private to `ScenarioParser`, used by Tasks 4 and 5):

```csharp
    /// <summary>One arm's walk result.</summary>
    private readonly record struct ArmWalk(Dictionary<ILocalSymbol, VarSource> Vars, List<int> Waits, bool Ok);

    /// <summary>Walks one arm under Guard(conditionIndex, arm), with <paramref name="bind"/> applied
    /// to its definition map first (an arm's pattern variables).</summary>
    private ArmWalk WalkArm(IEnumerable<StatementSyntax> body, int conditionIndex, int arm,
        Dictionary<ILocalSymbol, VarSource> parentVars, IReadOnlyDictionary<ILocalSymbol, VarSource>? bind = null);

    /// <summary>Rejoins after a branch: one N-way merge per local the arms define differently, then
    /// Advance. Returns false when a merge was refused.</summary>
    private bool Rejoin(SyntaxNode branch, int conditionIndex, Dictionary<ILocalSymbol, VarSource> parentVars,
        IReadOnlyList<ArmWalk> arms, bool ok);
```

- [ ] **Step 1: Write the failing test** (the merge type must be the local's, not the first arm's)

Add to `ConditionalLoweringTests.cs`:

```csharp
    [Fact]
    public async Task A_merge_takes_the_locals_type_when_arms_produce_subtypes()
    {
        // The arms return different subtypes of the local's type; the merge must be read as the
        // local's type, or the generated code reading it does not compile.
        var source = SampleSources.ConditionalDsl +
            """

            public sealed class SubtypeMergeScenarios : CondSuite
            {
                [Scenario("subtype merge")]
                public async Task Run()
                {
                    var patient = await Given.PatientExists("Alice");
                    object booking;
                    if (await Given.IsPriority())
                        booking = await When.CreateUrgent(patient);
                    else
                        booking = await Given.PatientExists("Bob");
                    await Then.Logged(booking);
                }
            }
            """;

        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();
        Assert.Empty(await GeneratorHarness.DiagnoseAsync(source));
    }
```

Add this step to `CondThen` in `SampleSources.ConditionalDsl`. On `main`, scenarios are instance
methods of a class deriving from the DSL's suite, `CondSuite`, and steps are instance methods of
`CondGiven`, `CondWhen` and `CondThen`:

```csharp
            [StepName("{value} is logged")]
            public Task Logged(object value) => Task.CompletedTask;
```

- [ ] **Step 2: Run it to see it fail**

Run: `cd /c/dev/raun-issue2 && dotnet test test/Raun.Generator.Test --filter-method "*subtypes*"`
Expected: FAIL with a CS0029/CS0266 conversion error in generated code, because the merge is typed as
`Appointment`.

- [ ] **Step 3: Implement the N-arm walk and rejoin**

Replace `ParseIf`'s body after the `ParseCondition` call, and the helpers it uses, with this
N-arm form. Keep `ParseCondition` as it is.

```csharp
        var conditionIndex = condition?.Index ?? -1;
        var parentVars = new Dictionary<ILocalSymbol, VarSource>(_vars, SymbolEqualityComparer.Default);

        var arms = new List<ArmWalk>
        {
            WalkArm([statement.Statement], conditionIndex, arm: 0, parentVars),
            WalkArm(statement.Else is { } elseClause ? [elseClause.Statement] : [], conditionIndex, arm: 1, parentVars),
        };

        return Rejoin(statement, conditionIndex, parentVars, arms, ok: condition is not null) && condition is not null;
```

```csharp
    private ArmWalk WalkArm(
        IEnumerable<StatementSyntax> body,
        int conditionIndex,
        int arm,
        Dictionary<ILocalSymbol, VarSource> parentVars,
        IReadOnlyDictionary<ILocalSymbol, VarSource>? bind = null)
    {
        var savedFrontier = _prevFrontier;
        var savedWaits = _pendingWaits;
        _vars.Clear();
        foreach (var pair in parentVars)
        {
            _vars[pair.Key] = pair.Value;
        }

        foreach (var pair in bind ?? new Dictionary<ILocalSymbol, VarSource>())
        {
            _vars[pair.Key] = pair.Value;
        }

        _guards.Add(new ParsedGuard(conditionIndex, arm));
        _prevFrontier = conditionIndex >= 0 ? [conditionIndex] : [];
        _pendingWaits = [];
        var ok = true;
        foreach (var statement in body)
        {
            ok &= ParseStatement(statement);
        }

        _guards.RemoveAt(_guards.Count - 1);

        var tail = new List<int>(_prevFrontier);
        tail.AddRange(_pendingWaits);
        _prevFrontier = savedFrontier;
        _pendingWaits = savedWaits;

        return new ArmWalk(new Dictionary<ILocalSymbol, VarSource>(_vars, SymbolEqualityComparer.Default), tail, ok);
    }
```

```csharp
    private bool Rejoin(
        SyntaxNode branch,
        int conditionIndex,
        Dictionary<ILocalSymbol, VarSource> parentVars,
        IReadOnlyList<ArmWalk> arms,
        bool ok)
    {
        ok &= arms.All(a => a.Ok);

        _vars.Clear();
        foreach (var pair in parentVars)
        {
            _vars[pair.Key] = pair.Value;
        }

        var frontier = new List<int>();
        foreach (var local in DifferingLocals(parentVars, arms))
        {
            // Nothing to merge when the branch is broken, or when a failed statement defined the
            // local: the local stays failed, already reported where it went wrong.
            var definitions = arms.Select(a => a.Vars.TryGetValue(local, out var s) ? s : null)
                .Append(parentVars.TryGetValue(local, out var p) ? p : null);
            if (!ok || definitions.Any(source => source is FailedOutput))
            {
                _vars[local] = FailedOutput.Instance;
                continue;
            }

            var mergeIndex = InsertMerge(local, conditionIndex, parentVars, arms);
            if (mergeIndex < 0)
            {
                Refuse(branch, "'" + local.Name + "' holds an array group, which cannot be merged across the arms of a branch; bind each arm's group to its own local");
                _vars[local] = FailedOutput.Instance;
                ok = false;
                continue;
            }

            frontier.Add(mergeIndex);
        }

        if (conditionIndex < 0)
        {
            return false;
        }

        // An empty arm's frontier is the condition itself, which the next statement already depends on.
        var waits = new SortedSet<int>(arms.SelectMany(a => a.Waits));
        waits.Remove(conditionIndex);

        // A following statement must never DEPEND on an arm's node (DependsOn is all-of and an arm may
        // not run); it joins on the merges, or on the condition when there are none. It must still
        // WAIT for every arm's last steps, or it would run concurrently with the inside of the branch —
        // that is what WaitsFor carries, and a not-taken arm does not cascade through it.
        Advance(frontier.Count > 0 ? frontier : [conditionIndex], [.. waits]);
        return ok;
    }
```

```csharp
    /// <summary>Locals whose definition differs between the arms (or between an arm and the parent) —
    /// exactly the set that needs a phi. A local declared inside one arm is branch-local (absent from
    /// the parent and from the other arms) and C# scoping forbids its later use, so it is dropped; a
    /// local declared before the branch without a value and assigned in EVERY arm is the ordinary
    /// phi. Ordered by name, then declaration position, so merges are inserted identically every run.</summary>
    private static IEnumerable<ILocalSymbol> DifferingLocals(
        Dictionary<ILocalSymbol, VarSource> parentVars, IReadOnlyList<ArmWalk> arms)
    {
        var locals = arms.SelectMany(a => a.Vars.Keys)
            .Distinct<ILocalSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(l => l.Name, System.StringComparer.Ordinal)
            .ThenBy(l => l.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0);

        foreach (var local in locals)
        {
            var inParent = parentVars.TryGetValue(local, out var parentDef);
            if (!inParent && !arms.All(a => a.Vars.ContainsKey(local)))
            {
                continue;
            }

            var defs = arms.Select(a => a.Vars.TryGetValue(local, out var s) ? s : parentDef).ToList();
            if (defs.Distinct().Count() > 1)
            {
                yield return local;
            }
        }
    }
```

```csharp
    /// <summary>
    /// Inserts the phi for one local: a synthetic merge with one source per arm — the arm's own
    /// definition, or, when the arm left the local alone, a PASS-THROUGH aliasing the parent
    /// definition, guarded on that arm. Every source is guarded on a different arm of one condition,
    /// so they are mutually exclusive, as <c>ScenarioDefinition.Validate</c> requires. Typed as the
    /// LOCAL, not as any one arm's producer, since arms may produce different subtypes. Arrays are not
    /// mergeable; returns -1 and the caller refuses the shape.
    /// </summary>
    private int InsertMerge(
        ILocalSymbol local,
        int conditionIndex,
        Dictionary<ILocalSymbol, VarSource> parentVars,
        IReadOnlyList<ArmWalk> arms)
    {
        var sources = new List<int>();
        for (var arm = 0; arm < arms.Count; arm++)
        {
            int source;
            if (arms[arm].Vars.TryGetValue(local, out var armSource))
            {
                source = armSource is StepOutput armStep ? armStep.Index : -1;
            }
            else
            {
                source = parentVars.TryGetValue(local, out var parentSource) && parentSource is StepOutput parentStep
                    ? InsertPassThrough(local, conditionIndex, arm, parentStep.Index)
                    : -1;
            }

            if (source < 0)
            {
                return -1;
            }

            sources.Add(source);
        }

        var phase = _steps.First(s => s.Index == sources[0]).Phase;
        var index = _nextIndex++;
        _steps.Add(new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, "merge:" + local.Name + ":" + index),
            Phase = phase,
            OperationName = "Merge",
            HasResult = true,
            ResultType = TypeSyntaxFactory.From(local.Type),
            DisplayNameTemplate = "«merge " + local.Name + "»",
            MergeSources = sources,
            IsSynthetic = true,
            Guards = [.. _guards],
            DependsOn = [],
        });

        _vars[local] = new StepOutput(index);
        return index;
    }
```

```csharp
    /// <summary>
    /// Stands in for an arm that did not redefine the local: a synthetic node aliasing the parent
    /// definition, guarded on the arm it OCCUPIES, so a merge's sources stay mutually exclusive and
    /// the parent value flows through when that arm runs.
    /// </summary>
    private int InsertPassThrough(ILocalSymbol local, int conditionIndex, int arm, int parentDef)
    {
        var producer = _steps.First(s => s.Index == parentDef);
        var index = _nextIndex++;
        var guards = new List<ParsedGuard>(_guards) { new(conditionIndex, arm) };
        _steps.Add(new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, "phi:" + local.Name + ":" + index),
            Phase = producer.Phase,
            OperationName = "Unchanged",
            HasResult = true,
            ResultType = TypeSyntaxFactory.From(local.Type),
            DisplayNameTemplate = "«" + local.Name + " unchanged»",
            MergeSources = [parentDef],
            IsSynthetic = true,
            Guards = guards,
            DependsOn = [],
        });

        return index;
    }
```

**Ordering note.** The old code built the *then* side first and then the *else* side; this loop
walks arms in order, so the pass-through and merge indices come out the same and snapshots must not
move. If `Conditional_scenario` moves, compare the node order and fix the loop, not the snapshot.

- [ ] **Step 4: Run all generator tests**

Run: `cd /c/dev/raun-issue2 && dotnet test test/Raun.Generator.Test 2>&1 | grep -E "^failed|failed:|succeeded:"`
Expected: `failed: 0`, the new test included, and no snapshot moves. The merge's `ResultType` is
read only through `__inputs.Get<T>` in consumers, and the existing samples' locals have the same type
as their producers.

- [ ] **Step 5: Commit**

```bash
cd /c/dev/raun-issue2 && jj describe -m "refactor(generator): branches walk and rejoin N arms

The arm walk, the definition-map rejoin and the phi insertion take any
number of arms; if/else is two. A merge (and a pass-through) is typed as the
local, not the first arm's producer, so arms producing different subtypes
of it merge cleanly." && jj new
```

---

### Task 4: `is`-pattern conditions and case nodes

**Files:**
- Modify: `src/Raun.Generator/Lowering/ScenarioParser.cs` (`ParseCondition`, `ParseIf`, `VarSource`,
  `Spell`, new `CaseVariable`, new `AddCaseNode`)
- Modify: `src/Raun.Generator/Lowering/Ir.cs` (`ParsedStep.CaseBinding`)
- Modify: `src/Raun.Generator/Emit/ScenarioEmitter.cs` (`SelectArmLambda` Pattern form; case-node
  `Invoke`)
- Modify: `src/Raun.Generator/Diagnostics/Descriptors.cs:99-106` (the RAUN011 message)
- Test: `test/Raun.Generator.Test/PatternBranchLoweringTests.cs` (new),
  `test/Raun.Generator.Test/SampleSources.cs`, `test/Raun.Generator.Test/LoweringCoverageTests.cs`

**Interfaces:**
- Consumes: `ArmWalk`, `WalkArm(..., bind)`, `Rejoin` (Task 3); `ParsedBranch`, `BranchForm` (Task 2).
- Produces:
  - **`ParsedStep.CaseBinding`:**

```csharp
    /// <summary>For a case node: re-runs one arm's pattern on the condition's recorded value and
    /// returns the pattern's variables. Null for every other node.</summary>
    public ParsedCaseBinding? CaseBinding { get; init; }
```

  - **`ParsedCaseBinding`:**

```csharp
internal sealed record ParsedCaseBinding
{
    private readonly Syn<TypeSyntax> _valueType;
    private readonly Syn<ExpressionSyntax> _match;
    private readonly Syn<ExpressionSyntax> _result;

    public int ConditionIndex { get; init; }

    /// <summary>The condition step's result type.</summary>
    public TypeSyntax ValueType { get => _valueType.Node!; init => _valueType = value; }

    /// <summary>A bool expression over <c>__v</c> that matches the arm and declares its variables,
    /// e.g. <c>__v is Rejected rejected</c> or <c>!(__v is not Rejected rejected)</c>.</summary>
    public ExpressionSyntax Match { get => _match.Node!; init => _match = value; }

    /// <summary>The variables: <c>rejected</c>, or <c>(big, total)</c>.</summary>
    public ExpressionSyntax Result { get => _result.Node!; init => _result = value; }
}
```

  - **`ScenarioParser.AddCaseNode`**, used by Task 5:

```csharp
    private IReadOnlyDictionary<ILocalSymbol, VarSource>? AddCaseNode(
        int conditionIndex, int arm, TypeSyntax valueType, ExpressionSyntax match, IReadOnlyList<ILocalSymbol> variables, string label)
```

  - **`VarSource` shape `CaseVariable(int Index, int Element, int Count, TypeSyntax Type)`.** Spelled
    as `__inputs.Get<Type>(Index)` when `Count == 1`, and `__inputs.Get<Type>(Index).Item{Element+1}`
    otherwise.

- [ ] **Step 1: Add the sample DSL and scenarios**

Append to `SampleSources.cs`. Shape it like the other DSLs on `main` after #3: a world, step classes
deriving from `Given<W>`, `When<W>` and `Then<W>`, and a `Scenarios<W>` base. Copy the boilerplate
from `SampleSources.ConditionalDsl` exactly and change only the names and steps.

```csharp
    // Pattern branching: a step returns one of several outcome records; scenarios branch on which.
    public const string OutcomeDsl =
        """
        using System.Threading.Tasks;
        using Raun;

        namespace OutcomeDemo;

        public abstract record Outcome;
        public sealed record Accepted(string Shipment, decimal Total, bool Express) : Outcome;
        public sealed record Rejected(string Reason) : Outcome;
        public sealed record Pending : Outcome;

        public sealed partial class OutcomeWhen : When<NoWorld>
        {
            [StepName("submitting order {id}")]
            public Task<Outcome> Submit(string id) => Task.FromResult<Outcome>(id switch
            {
                "ok" => new Accepted("S1", 10m, false),
                "big" => new Accepted("S2", 5000m, false),
                "fast" => new Accepted("S3", 10m, true),
                "no" => new Rejected("out of stock"),
                _ => new Pending(),
            });
        }

        public sealed partial class OutcomeThen : Then<NoWorld>
        {
            [StepName("shipment {shipment} scheduled")]
            public Task Shipped(string shipment) => Task.CompletedTask;

            [StepName("customer told {reason}")]
            public Task Told(string reason) => Task.CompletedTask;

            [StepName("review of {total:0.00}")]
            public Task Review(decimal total) => Task.CompletedTask;

            [StepName("order pending")]
            public Task StillPending() => Task.CompletedTask;
        }

        public abstract class OutcomeScenarios : Scenarios<NoWorld>
        {
            public OutcomeWhen When => Steps<OutcomeWhen>();
            public OutcomeThen Then => Steps<OutcomeThen>();
        }
        """;

    public const string IsPatternScenario =
        """

        public sealed class IsPatternScenarios : OutcomeScenarios
        {
            [Scenario("rejected order tells the customer")]
            public async Task Rejection()
            {
                if (await When.Submit("no") is Rejected rejected)
                    await Then.Told(rejected.Reason);
                else
                    await Then.StillPending();
            }

            [Scenario("not rejected order ships")]
            public async Task NotRejected()
            {
                if (await When.Submit("ok") is not Accepted accepted)
                    await Then.StillPending();
                else
                    await Then.Shipped(accepted.Shipment);
            }
        }
        """;
```

Add `[nameof(SampleSources.IsPatternScenario)] = SampleSources.OutcomeDsl,` to
`LoweringCoverageTests.DslFor`.

- [ ] **Step 2: Write the failing tests**

Create `test/Raun.Generator.Test/PatternBranchLoweringTests.cs`:

```csharp
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// `if (await S is P)`: the condition is the step; the pattern picks the arm (arm 0 matches, arm 1
/// is the else); the pattern's variables are step outputs inside the arm that binds them, read
/// through a hidden case node.
/// </summary>
public class PatternBranchLoweringTests
{
    private static GeneratorResult Generate() => GeneratorHarness.Run(SampleSources.OutcomeDsl + SampleSources.IsPatternScenario);

    private static ScenarioDefinition Scenario(GeneratorResult result, string method)
        => result.Definitions().Single(d => d.MethodName.EndsWith("." + method, StringComparison.Ordinal));

    [Fact]
    public void An_is_pattern_condition_has_two_arms_labelled_by_its_pattern()
    {
        var result = Generate();
        result.AssertCompiles();
        var condition = Scenario(result, "Rejection").Nodes.Single(n => n.OperationName == "Submit");

        Assert.NotNull(condition.SelectArm);
        Assert.Equal(["is Rejected rejected", "else"], condition.Arms);
    }

    [Fact]
    public void A_pattern_variable_is_read_through_a_case_node_guarded_on_its_arm()
    {
        var def = Scenario(Generate(), "Rejection");
        var told = def.Nodes.Single(n => n.OperationName == "Told");
        var caseNode = def.Nodes.Single(n => n.IsSynthetic && n.OperationName == "Case");

        Assert.Equal([new Guard(caseNode.DependsOn[0], 0)], caseNode.Guards.Where(g => g.ConditionIndex == caseNode.DependsOn[0]));
        Assert.Contains(caseNode.Index, told.DependsOn);
    }

    [Fact]
    public async Task The_matching_arm_runs_with_the_bound_value_and_the_other_is_not_taken()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "Rejection").RunAsync();

        var told = results.Single(r => r.Node.OperationName == "Told");
        Assert.Equal(StepStatus.Passed, told.Status);
        Assert.Equal("customer told out of stock", told.DisplayName);
        var pending = results.Single(r => r.Node.OperationName == "StillPending");
        Assert.Equal(StepStatus.NotTaken, pending.Status);
        Assert.Equal("not taken: Submit took is Rejected rejected", pending.SkipReason);
    }

    [Fact]
    public async Task An_is_not_pattern_binds_its_variable_in_the_else_arm()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "NotRejected").RunAsync();

        Assert.Equal(StepStatus.Passed, results.Single(r => r.Node.OperationName == "Shipped").Status);
        Assert.Equal("shipment S1 scheduled", results.Single(r => r.Node.OperationName == "Shipped").DisplayName);
        Assert.Equal(StepStatus.NotTaken, results.Single(r => r.Node.OperationName == "StillPending").Status);
    }

    [Fact]
    public async Task A_compound_condition_is_refused_with_the_reason()
    {
        var source = SampleSources.OutcomeDsl +
            """

            public sealed class CompoundScenarios : OutcomeScenarios
            {
                [Scenario("compound")]
                public async Task Run()
                {
                    if (await When.Submit("no") is Rejected && true)
                        await Then.StillPending();
                }
            }
            """;

        var diagnostic = Assert.Single(await GeneratorHarness.DiagnoseAsync(source));

        Assert.Equal("RAUN011", diagnostic.Id);
    }
}
```

- [ ] **Step 3: Run them to see them fail**

Run: `cd /c/dev/raun-issue2 && dotnet test test/Raun.Generator.Test --filter-class "*PatternBranchLoweringTests"`
Expected: FAIL with RAUN011, because `is` conditions are refused today.

- [ ] **Step 4: Implement**

1. In `ParseCondition`, accept `IsPatternExpressionSyntax { Expression: AwaitExpressionSyntax { Expression: InvocationExpressionSyntax } }`.
   It returns the condition step *and* the branch. Change its signature to
   `private (ParsedStep Step, ParsedBranch Branch, IReadOnlyList<ILocalSymbol> PatternVariables)? ParseCondition(ExpressionSyntax condition)`.

```csharp
        if (condition is IsPatternExpressionSyntax { Expression: AwaitExpressionSyntax { Expression: InvocationExpressionSyntax patternCall } } isPattern)
        {
            var call = ResolveCall(patternCall, Descriptors.InvalidCondition);
            if (call is null || call.ResultType is null)
            {
                if (call is not null)
                {
                    Report(Descriptors.InvalidCondition, condition);
                }

                return null;
            }

            var lowered = ArgumentLowering.Lower(_model, isPattern.Pattern, StepValueFor(loop: null));
            foreach (var violation in lowered.Violations)
            {
                Report(Descriptors.InvalidArgument, violation.Node, violation.Subject, violation.Reason);
            }

            if (lowered.Violations.Count > 0)
            {
                return null;
            }

            var step = BuildStep(call, groupId: null, _prevFrontier);
            var branch = new ParsedBranch
            {
                Form = BranchForm.Pattern,
                ValueType = step.ResultType,
                Pattern = lowered.Node,
                ArmLabels = ["is " + isPattern.Pattern.ToString(), "else"],
            };
            MarkAsCondition(step, branch);
            return (step, branch, PatternVariables(isPattern.Pattern));
        }
```

   `StepValueFor(loop)` is the existing local function `StepValue` from `ResolveCall`, lifted into a
   private method so that patterns and `when` clauses use the same spelling:

```csharp
    private System.Func<ISymbol, ExpressionSyntax?> StepValueFor(LoopElement? loop) => symbol => symbol switch
    {
        ILocalSymbol local when _vars.TryGetValue(local, out var source)
            => source is FailedOutput ? IdentifierName(local.Name) : Spell(source),
        IParameterSymbol parameter when loop is { } element
            && SymbolEqualityComparer.Default.Equals(parameter, element.Variable) => Num(element.Value),
        _ => null,
    };
```

   `ResolveCall` then uses `StepValueFor(loop)`.

```csharp
    /// <summary>The variables a pattern declares (<c>Rejected r</c>, <c>{ Total: var t }</c>), in order.</summary>
    private List<ILocalSymbol> PatternVariables(PatternSyntax pattern)
        => pattern.DescendantNodesAndSelf()
            .OfType<SingleVariableDesignationSyntax>()
            .Select(d => _model.GetDeclaredSymbol(d))
            .OfType<ILocalSymbol>()
            .ToList();
```

   The existing boolean form returns `(step, Truth branch, [])`.

2. In `ParseIf`, bind the pattern variables. A positive pattern binds them in arm 0. A top-level
   `not` pattern binds them in arm 1, where C#'s definite assignment puts them:

```csharp
        var parsed = ParseCondition(statement.Condition);
        var conditionIndex = parsed?.Step.Index ?? -1;
        var parentVars = new Dictionary<ILocalSymbol, VarSource>(_vars, SymbolEqualityComparer.Default);

        IReadOnlyDictionary<ILocalSymbol, VarSource>? bind0 = null, bind1 = null;
        if (parsed is { Branch.Form: BranchForm.Pattern } p && p.PatternVariables.Count > 0)
        {
            var negated = p.Branch.Pattern is UnaryPatternSyntax { OperatorToken.RawKind: (int)SyntaxKind.NotKeyword };
            var vIs = IsPatternExpression(IdentifierName("__v"), p.Branch.Pattern!);
            var match = negated
                ? PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, ParenthesizedExpression(vIs))
                : (ExpressionSyntax)vIs;
            var binding = AddCaseNode(conditionIndex, negated ? 1 : 0, p.Step.ResultType, match, p.PatternVariables, p.Branch.ArmLabels[negated ? 1 : 0]);
            if (negated) bind1 = binding; else bind0 = binding;
        }

        var arms = new List<ArmWalk>
        {
            WalkArm([statement.Statement], conditionIndex, arm: 0, parentVars, bind0),
            WalkArm(statement.Else is { } elseClause ? [elseClause.Statement] : [], conditionIndex, arm: 1, parentVars, bind1),
        };

        return Rejoin(statement, conditionIndex, parentVars, arms, ok: parsed is not null) && parsed is not null;
```

3. Add the case node and the new `VarSource` shape:

```csharp
    /// <summary>A pattern variable: element <paramref name="Element"/> of a case node's result
    /// (the value itself when the case binds one variable).</summary>
    private sealed record CaseVariable(int Index, int Element, int Count, TypeSyntax Type) : VarSource
    {
        public override IEnumerable<int> Producers => [Index];
    }
```

```csharp
    /// <summary>
    /// Adds the hidden case node of one arm — guarded on that arm, depending on the condition — which
    /// re-matches the arm's pattern on the recorded value and returns its variables, and returns the
    /// arm's bindings of those variables to it. Null when the pattern declares none.
    /// </summary>
    private IReadOnlyDictionary<ILocalSymbol, VarSource>? AddCaseNode(
        int conditionIndex, int arm, TypeSyntax valueType, ExpressionSyntax match, IReadOnlyList<ILocalSymbol> variables, string label)
    {
        if (variables.Count == 0 || conditionIndex < 0)
        {
            return null;
        }

        var resultType = variables.Count == 1
            ? TypeSyntaxFactory.From(variables[0].Type)
            : TupleType(SeparatedList(variables.Select(v => TupleElement(TypeSyntaxFactory.From(v.Type)))));
        ExpressionSyntax result = variables.Count == 1
            ? IdentifierName(variables[0].Name)
            : TupleExpression(SeparatedList(variables.Select(v => Argument(IdentifierName(v.Name)))));

        var index = _nextIndex++;
        _steps.Add(new ParsedStep
        {
            Index = index,
            StepId = GenStableId.ForStep(_scenarioId, "case:" + conditionIndex + ":" + arm),
            Phase = _steps.First(s => s.Index == conditionIndex).Phase,
            OperationName = "Case",
            HasResult = true,
            ResultType = resultType,
            DisplayNameTemplate = "«" + label + "»",
            IsSynthetic = true,
            Guards = [.. _guards, new ParsedGuard(conditionIndex, arm)],
            DependsOn = [conditionIndex],
            CaseBinding = new ParsedCaseBinding
            {
                ConditionIndex = conditionIndex,
                ValueType = valueType,
                Match = match,
                Result = result,
            },
        });

        var bindings = new Dictionary<ILocalSymbol, VarSource>(SymbolEqualityComparer.Default);
        for (var i = 0; i < variables.Count; i++)
        {
            bindings[variables[i]] = new CaseVariable(index, i, variables.Count, resultType);
        }

        return bindings;
    }
```

   Extend `Spell`:

```csharp
        CaseVariable variable => variable.Count == 1
            ? InputsGet(variable.Type, variable.Index)
            : MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                InputsGet(variable.Type, variable.Index),
                IdentifierName("Item" + (variable.Element + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))),
```

   In `InsertMerge`/`Rejoin`, a `CaseVariable` is never an arm's definition of an *outer* local,
   because pattern variables are arm-scoped, so nothing more is needed there. In `DifferingLocals`,
   pattern variables are branch-local and are dropped by the existing rule.

4. In `ScenarioEmitter`:

   - Add to `SelectArmLambda`:

```csharp
            BranchForm.Pattern => ConditionalExpression(
                IsPatternExpression(ParenthesizedExpression(value), branch.Pattern!), Num(0), Num(1)),
```

   - In `BuildNode`, when `step.CaseBinding is { } binding`, set `Invoke` to:

```csharp
    /// <summary>A case node's body: <c>static (__inputs, __ctx) => { var __v = __inputs.Get&lt;T&gt;(c);
    /// if (MATCH) return Task.FromResult&lt;object?&gt;(RESULT); throw … }</c>. Its guard means the
    /// arm was chosen, so MATCH holds; the throw guards a generator bug.</summary>
    private static ParenthesizedLambdaExpressionSyntax CaseInvokeLambda(ParsedCaseBinding binding)
        => InvokeLambda().WithBlock(Block(
            LocalDeclarationStatement(VariableDeclaration(IdentifierName("var"))
                .WithVariables(SingletonSeparatedList(VariableDeclarator(Identifier("__v"))
                    .WithInitializer(EqualsValueClause(InputsGet(binding.ValueType, binding.ConditionIndex)))))),
            IfStatement(binding.Match, ReturnStatement(TaskFromResult(binding.Result))),
            ThrowStatement(ObjectCreationExpression(Names.Global("System", "InvalidOperationException"))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(
                    Lit("A case node ran but its arm's pattern did not match; this is a Raun generator bug."))))))));

    /// <summary><c>global::System.Threading.Tasks.Task.FromResult&lt;object?&gt;(value)</c>.</summary>
    private static InvocationExpressionSyntax TaskFromResult(ExpressionSyntax value)
        => InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    Names.Global("System", "Threading", "Tasks", "Task"),
                    GenericName(Identifier("FromResult"))
                        .WithTypeArgumentList(TypeArgumentList(SingletonSeparatedList<TypeSyntax>(
                            NullableType(PredefinedType(Token(SyntaxKind.ObjectKeyword))))))))
            .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(value))));
```

   - Refactor the existing synthetic-node `Invoke` (`Task.FromResult<object?>(null)`) to call
     `TaskFromResult(LiteralExpression(SyntaxKind.NullLiteralExpression))`. This is
     byte-identical, so no snapshot moves.

5. Change the RAUN011 message in `Descriptors.cs` to:
   `"A scenario branch must be 'if (await <step>)', 'if (await <step> is <pattern>)', or 'switch (await <step>)'; the condition here is not — a step's result must be what the branch decides on, and only an 'is' pattern may test it"`.
   Then update any test asserting the old text.

- [ ] **Step 5: Run the tests**

Run: `cd /c/dev/raun-issue2 && dotnet test test/Raun.Generator.Test 2>&1 | grep -E "^failed|failed:|succeeded:"`
Expected: `failed: 0`, the coverage guard for `IsPatternScenario` included.

- [ ] **Step 6: Commit**

```bash
cd /c/dev/raun-issue2 && jj describe -m "feat(generator): branch on a step's result with an is pattern

if (await S is P) and if (await S is not P) branch on what a step returned:
SelectArm is the compiler's own 'is' over the recorded value, and the
pattern's variables are step outputs inside the arm that binds them (arm 0
for a positive pattern, the else for a top-level 'not'), read through a
hidden case node that re-matches the arm's pattern. Compound conditions
stay RAUN011, now saying which forms branch." && jj new
```

---

### Task 5: `switch` on a step result

**Files:**
- Modify: `src/Raun.Generator/Lowering/ScenarioParser.cs` (`ParseStatement`, new `ParseSwitch`)
- Modify: `src/Raun.Generator/Emit/ScenarioEmitter.cs` (`SelectArmLambda` Switch form)
- Test: `test/Raun.Generator.Test/SwitchBranchLoweringTests.cs` (new), `SampleSources.cs`,
  `LoweringCoverageTests.cs`, `GeneratorSnapshotTests.cs` (one new snapshot)

**Interfaces:**
- Consumes: `WalkArm`, `Rejoin`, `ArmWalk` (Task 3); `AddCaseNode`, `PatternVariables`,
  `StepValueFor`, `CaseVariable` (Task 4); `ParsedBranch.Sections` (Task 2).

- [ ] **Step 1: Add the sample scenario**

```csharp
    public const string SwitchScenario =
        """

        public sealed class SwitchScenarios : OutcomeScenarios
        {
            [Scenario("an order is routed by its outcome")]
            public async Task Route()
            {
                switch (await When.Submit("big"))
                {
                    case Accepted { Total: > 1000 } big:
                        await Then.Review(big.Total);
                        break;
                    case Accepted accepted when accepted.Express:
                        await Then.Shipped(accepted.Shipment);
                        break;
                    case Rejected rejected:
                        await Then.Told(rejected.Reason);
                        break;
                    default:
                        await Then.StillPending();
                        break;
                }
            }

            [Scenario("no arm matches")]
            public async Task NoMatch()
            {
                switch (await When.Submit("pending"))
                {
                    case Accepted accepted:
                        await Then.Shipped(accepted.Shipment);
                        break;
                    case Rejected rejected:
                        await Then.Told(rejected.Reason);
                        break;
                }

                await Then.StillPending();
            }

            [Scenario("a local merges across switch arms")]
            public async Task Merge()
            {
                var outcome = await When.Submit("ok");
                switch (await When.Submit("no"))
                {
                    case Rejected:
                        outcome = await When.Submit("fast");
                        break;
                    default:
                        break;
                }

                if (await When.Submit("x") is Pending)
                    await Then.Shipped(((Accepted)outcome).Shipment);
            }
        }
        """;
```

Add `[nameof(SampleSources.SwitchScenario)] = SampleSources.OutcomeDsl,` to `LoweringCoverageTests.DslFor`.

- [ ] **Step 2: Write the failing tests**

Create `test/Raun.Generator.Test/SwitchBranchLoweringTests.cs`:

```csharp
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// `switch (await S)`: one arm per section, chosen by the compiler's own switch over the recorded
/// value; pattern variables are case-node outputs; `when` clauses lower like step arguments and their
/// step reads become the condition's dependencies; no match leaves every arm not taken.
/// </summary>
public class SwitchBranchLoweringTests
{
    private static GeneratorResult Generate() => GeneratorHarness.Run(SampleSources.OutcomeDsl + SampleSources.SwitchScenario);

    private static ScenarioDefinition Scenario(GeneratorResult result, string method)
        => result.Definitions().Single(d => d.MethodName.EndsWith("." + method, StringComparison.Ordinal));

    [Fact]
    public void A_switch_has_one_arm_per_section_labelled_as_written()
    {
        var result = Generate();
        result.AssertCompiles();
        var condition = Scenario(result, "Route").Nodes.First(n => n.OperationName == "Submit");

        Assert.Equal(
            ["case Accepted { Total: > 1000 } big", "case Accepted accepted when accepted.Express", "case Rejected rejected", "default"],
            condition.Arms);
    }

    [Fact]
    public async Task The_first_matching_section_runs_with_its_variables()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "Route").RunAsync();

        var review = results.Single(r => r.Node.OperationName == "Review");
        Assert.Equal(StepStatus.Passed, review.Status);
        Assert.Equal("review of 5000.00", review.DisplayName);
        Assert.All(
            results.Where(r => r.Node.OperationName is "Shipped" or "Told" or "StillPending"),
            r => Assert.Equal(StepStatus.NotTaken, r.Status));
    }

    [Fact]
    public async Task No_match_leaves_every_arm_not_taken_and_the_scenario_continues()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "NoMatch").RunAsync();

        Assert.All(results.Where(r => r.Node.OperationName is "Shipped" or "Told"),
            r => Assert.Equal("not taken: Submit matched no arm", r.SkipReason));
        Assert.Equal(StepStatus.Passed, results.Last(r => r.Node.OperationName == "StillPending").Status);
    }

    [Fact]
    public async Task A_local_reassigned_in_some_arms_merges_n_ways()
    {
        var result = Generate();
        result.AssertCompiles();
        var def = Scenario(result, "Merge");

        var merge = def.Nodes.Single(n => n.OperationName == "Merge");
        Assert.Equal(2, merge.MergeSources.Count);
        var results = await def.RunAsync();
        Assert.Equal("shipment S3 scheduled", results.Single(r => r.Node.OperationName == "Shipped").DisplayName);
    }

    [Fact]
    public async Task A_when_clause_reading_an_earlier_step_makes_the_condition_depend_on_it()
    {
        var source = SampleSources.OutcomeDsl +
            """

            public sealed class WhenDependencyScenarios : OutcomeScenarios
            {
                [Scenario("when reads an earlier step")]
                public async Task Run()
                {
                    var first = await When.Submit("ok");
                    await Then.StillPending();
                    switch (await When.Submit("fast"))
                    {
                        case Accepted a when a.Shipment != ((Accepted)first).Shipment:
                            await Then.Shipped(a.Shipment);
                            break;
                    }
                }
            }
            """;

        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();
        var def = result.Definitions().Single();
        var first = def.Nodes.First(n => n.OperationName == "Submit");
        var condition = def.Nodes.Last(n => n.OperationName == "Submit");

        Assert.Contains(first.Index, condition.DependsOn);
        Assert.Equal(StepStatus.Passed, (await def.RunAsync()).Single(r => r.Node.OperationName == "Shipped").Status);
    }

    [Theory]
    [InlineData("case Rejected r: return;", "RAUN003")]
    [InlineData("case Rejected r: throw new System.Exception();", "RAUN003")]
    [InlineData("case Rejected r when (await When.Submit(\"x\")) is Pending: break;", "RAUN007")]
    public async Task A_refused_section_is_reported_once(string section, string id)
    {
        var source = SampleSources.OutcomeDsl +
            $$"""

            public sealed class RefusedSwitchScenarios : OutcomeScenarios
            {
                [Scenario("refused switch")]
                public async Task Run()
                {
                    switch (await When.Submit("no"))
                    {
                        {{section}}
                        default: break;
                    }
                }
            }
            """;

        Assert.Single(await GeneratorHarness.DiagnoseAsync(source), d => d.Id == id);
    }

    [Fact]
    public async Task A_switch_on_something_other_than_an_awaited_step_is_RAUN011()
    {
        var source = SampleSources.OutcomeDsl +
            """

            public sealed class NotAStepScenarios : OutcomeScenarios
            {
                [Scenario("switch on a constant")]
                public async Task Run()
                {
                    var outcome = await When.Submit("no");
                    switch (outcome)
                    {
                        default: await Then.StillPending(); break;
                    }
                }
            }
            """;

        Assert.Single(await GeneratorHarness.DiagnoseAsync(source), d => d.Id == "RAUN011");
    }
}
```

Also add a snapshot test to `GeneratorSnapshotTests.cs`, following that file's existing pattern
(look at `Conditional_scenario`):

```csharp
    [Fact]
    public void Switch_scenario() => VerifyGenerated(SampleSources.OutcomeDsl + SampleSources.SwitchScenario);
```

Use whatever helper `Conditional_scenario` uses, under the same name.

- [ ] **Step 3: Run them to see them fail**

Run: `cd /c/dev/raun-issue2 && dotnet test test/Raun.Generator.Test --filter-class "*SwitchBranchLoweringTests"`
Expected: FAIL with RAUN003, because `switch` is refused as control flow today.

- [ ] **Step 4: Implement `ParseSwitch`**

In `ParseStatement`, add a case **before** the control-flow list. `SwitchStatementSyntax` is
currently in that list; remove it from there.

```csharp
            case SwitchStatementSyntax switchStatement:
                return ParseSwitch(switchStatement);
```

```csharp
    /// <summary>
    /// Lowers <c>switch (await S) { case …: …; break; … }</c>: the step is the condition; each section
    /// is an arm, chosen by the compiler's own switch over the recorded value (labels re-hosted like
    /// step arguments; a <c>when</c> clause's step reads become the condition's dependencies); a
    /// section's pattern variables are its case node's outputs; locals rejoin N-way.
    /// </summary>
    private bool ParseSwitch(SwitchStatementSyntax statement)
    {
        if (statement.Expression is not AwaitExpressionSyntax { Expression: InvocationExpressionSyntax invocation })
        {
            Report(Descriptors.InvalidCondition, statement.Expression);
            WalkSectionsForDiagnostics(statement);
            return false;
        }

        var call = ResolveCall(invocation, Descriptors.InvalidCondition);
        var ok = call is { ResultType: not null };
        if (call is { ResultType: null })
        {
            Report(Descriptors.InvalidCondition, statement.Expression);
        }

        // Lower every section's labels; collect the step outputs their when-clauses read.
        var sections = new List<SwitchSectionSyntax>();
        var labels = new List<string>();
        var whenReads = new SortedSet<int>();
        foreach (var section in statement.Sections)
        {
            var lowered = new List<SwitchLabelSyntax>();
            foreach (var label in section.Labels)
            {
                var result = ArgumentLowering.Lower(_model, label, StepValueFor(loop: null));
                foreach (var violation in result.Violations)
                {
                    Report(Descriptors.InvalidArgument, violation.Node, violation.Subject, violation.Reason);
                    ok = false;
                }

                foreach (var read in result.Reads)
                {
                    whenReads.UnionWith(_vars[read.Local].Producers);
                }

                lowered.Add(result.Node.WithoutTrivia());
            }

            sections.Add(SwitchSection(List(lowered), List<StatementSyntax>()));
            labels.Add(string.Join(" ", section.Labels.Select(l => l.ToString().TrimEnd(':').Trim())));
        }

        ParsedStep? condition = null;
        if (call is { ResultType: not null } && ok)
        {
            condition = BuildStep(call, groupId: null, [.. _prevFrontier.Concat(whenReads)]);
            MarkAsCondition(condition, new ParsedBranch
            {
                Form = BranchForm.Switch,
                ValueType = condition.ResultType,
                Sections = sections,
                ArmLabels = labels,
            });
        }

        var conditionIndex = condition?.Index ?? -1;
        var parentVars = new Dictionary<ILocalSymbol, VarSource>(_vars, SymbolEqualityComparer.Default);
        var arms = new List<ArmWalk>();
        for (var arm = 0; arm < statement.Sections.Count; arm++)
        {
            var section = statement.Sections[arm];
            var bind = condition is null ? null : BindSection(condition, arm, section, sections[arm], labels[arm]);
            arms.Add(WalkArm(SectionBody(section), conditionIndex, arm, parentVars, bind));
        }

        return Rejoin(statement, conditionIndex, parentVars, arms, ok: condition is not null) && condition is not null;
    }

    /// <summary>A section's statements without its closing <c>break;</c>; any other way out of a
    /// section (<c>return</c>, <c>throw</c>, <c>goto case</c>) stays in, and is RAUN003 when walked.</summary>
    private static IEnumerable<StatementSyntax> SectionBody(SwitchSectionSyntax section)
        => section.Statements.LastOrDefault() is BreakStatementSyntax
            ? section.Statements.Take(section.Statements.Count - 1)
            : section.Statements;

    /// <summary>The case node binding a section's pattern variables. A section with several labels
    /// binds none (C# forbids using them there).</summary>
    private IReadOnlyDictionary<ILocalSymbol, VarSource>? BindSection(
        ParsedStep condition, int arm, SwitchSectionSyntax written, SwitchSectionSyntax lowered, string label)
    {
        if (written.Labels.Count != 1 || written.Labels[0] is not CasePatternSwitchLabelSyntax patternLabel)
        {
            return null;
        }

        var variables = PatternVariables(patternLabel.Pattern);
        var loweredPattern = ((CasePatternSwitchLabelSyntax)lowered.Labels[0]).Pattern;
        return AddCaseNode(
            condition.Index, arm, condition.ResultType,
            IsPatternExpression(IdentifierName("__v"), loweredPattern), variables, label);
    }

    /// <summary>Under a broken governing expression, the sections are still walked (with no guard to
    /// hang them on) so their own problems are reported in the same build.</summary>
    private void WalkSectionsForDiagnostics(SwitchStatementSyntax statement)
    {
        var parentVars = new Dictionary<ILocalSymbol, VarSource>(_vars, SymbolEqualityComparer.Default);
        var arms = statement.Sections.Select((s, k) => WalkArm(SectionBody(s), -1, k, parentVars)).ToList();
        Rejoin(statement, -1, parentVars, arms, ok: false);
    }
```

**Arm labels:** they use the label text as written, with the colon trimmed:
`case Accepted accepted when accepted.Express`, `default`. A section with several labels joins them
with a space.

**`when` dependencies:** `ArgumentLowering.Lower` over a `CasePatternSwitchLabelSyntax` treats the
pattern's own variables as declared within the node, so they stay as written. Reads of earlier step
outputs come back in `Reads` and become dependencies of the condition.

- [ ] **Step 5: Emit the switch selector**

Add to `SelectArmLambda`. A switch becomes a block-bodied lambda:

```csharp
        if (branch.Form == BranchForm.Switch)
        {
            // switch (__inputs.Get<T>(c)) { <section k labels>: return k; … } return -1;
            var sections = branch.Sections.Select((section, k) =>
                section.WithStatements(SingletonList<StatementSyntax>(ReturnStatement(Num(k)))));
            return SimpleLambdaExpression(Parameter(Identifier("__inputs")))
                .WithModifiers(TokenList(Token(SyntaxKind.StaticKeyword)))
                .WithBlock(Block(
                    SwitchStatement(value).WithSections(List(sections)),
                    ReturnStatement(Num(-1))));
        }
```

Place it before the `ExpressionSyntax body = branch.Form switch` expression, and remove `Switch` from
that expression's throw arm. When a `default` section exists, C# sees the trailing `return -1;` as
unreachable code and warns (CS0162). Emit the trailing return only when no section has a
`DefaultSwitchLabelSyntax`:

```csharp
            var hasDefault = branch.Sections.Any(s => s.Labels.Any(l => l is DefaultSwitchLabelSyntax));
            var statements = new List<StatementSyntax> { SwitchStatement(value).WithSections(List(sections)) };
            if (!hasDefault)
            {
                statements.Add(ReturnStatement(Num(-1)));
            }
```

and use `Block(statements)`.

- [ ] **Step 6: Run the tests, then accept the one new snapshot**

Run: `cd /c/dev/raun-issue2 && dotnet test test/Raun.Generator.Test 2>&1 | grep -E "^failed|failed:|succeeded:"`
Expected: only `Switch_scenario` fails, because its snapshot does not exist yet.

```bash
cd /c/dev/raun-issue2 && RAUN_ACCEPT_SNAPSHOTS=1 dotnet test test/Raun.Generator.Test --filter-method "*Switch_scenario*"
```

Review the new verified file. The `Submit` condition has
`SelectArm = static __inputs => { switch (__inputs.Get<global::OutcomeDemo.Outcome>(N)) { … return 0; … } }`
with fully qualified type names in its patterns. There is a `Case` node per binding arm, and
`Arms = new string[] { … }`. Then re-run the whole suite and expect `failed: 0`.

- [ ] **Step 7: Commit**

```bash
cd /c/dev/raun-issue2 && jj describe -m "feat(generator): switch on a step's result

switch (await S) { … } branches N ways: each section is an arm, chosen by
the compiler's own switch over the recorded value, so any pattern, 'when'
clause and (with C# 15) union matching just works. A section ends in break;
return/throw/goto case stay RAUN003. Labels lower like step arguments: a
when-clause reading an earlier step makes the condition depend on it, and
await or a step call in one is RAUN007. Section pattern variables are
case-node outputs; locals rejoin N-way; no match leaves every arm not
taken." && jj new
```

---

### Task 6: C# 15 — unions, closed hierarchies, collection arguments

**Files:**
- Test: `test/Raun.Generator.Test/CSharp15BranchTests.cs` (new), `SampleSources.cs`,
  `LoweringCoverageTests.cs`
- Create: `samples/AppointmentTests/BookingOutcomeScenarios.cs` (+ its steps, in the sample's own
  DSL files)

**Interfaces:**
- Consumes: everything from Tasks 2-5. No new production code is expected here. If any test needs
  production changes, the earlier task missed a case; fix it there.

- [ ] **Step 1: Write the tests**

Add to `SampleSources.cs` a union and closed-hierarchy DSL. It uses the same scenario-class
boilerplate as `OutcomeDsl`:

```csharp
    public const string Csharp15Dsl =
        """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Raun;

        namespace Cs15Demo;

        public sealed record Booked(string Slot);
        public sealed record Waitlisted(int Position);
        public union BookingOutcome(Booked, Waitlisted, int);   // int: a value-type case

        public closed record class Gate;
        public sealed record Open(string By) : Gate;
        public sealed record Shut(string Reason) : Gate;

        public sealed partial class Cs15When : When<NoWorld>
        {
            [StepName("booking {who}")]
            public Task<BookingOutcome> Book(string who) => Task.FromResult<BookingOutcome>(who switch
            {
                "a" => new Booked("09:00"),
                "b" => new Waitlisted(3),
                _ => 42,
            });

            [StepName("the gate")]
            public Task<Gate> TheGate(bool open)
                => Task.FromResult<Gate>(open ? new Open("guard") : new Shut("closed for lunch"));
        }

        public sealed partial class Cs15Then : Then<NoWorld>
        {
            [StepName("{text} noted")]
            public Task Note(string text) => Task.CompletedTask;

            [StepName("{names.Count} names")]
            public Task Names(HashSet<string> names) => Task.CompletedTask;
        }

        public abstract class Cs15Suite : Scenarios<NoWorld>
        {
            public Cs15When When => Steps<Cs15When>();
            public Cs15Then Then => Steps<Cs15Then>();
        }
        """;

    public const string Csharp15Scenario =
        """

        public sealed class Cs15Scenarios : Cs15Suite
        {
            [Scenario("a union switch is exhaustive")]
            public async Task Union()
            {
                switch (await When.Book("c"))
                {
                    case Booked booked: await Then.Note(booked.Slot); break;
                    case Waitlisted waitlisted: await Then.Note("position " + waitlisted.Position); break;
                    case int code: await Then.Note("code " + code); break;
                }
            }

            [Scenario("a closed hierarchy switch is exhaustive")]
            public async Task Closed()
            {
                switch (await When.TheGate(false))
                {
                    case Open open: await Then.Note("opened by " + open.By); break;
                    case Shut shut: await Then.Note(shut.Reason); break;
                }
            }

            [Scenario("a collection expression argument")]
            public async Task CollectionArguments()
            {
                await Then.Names([with(StringComparer.OrdinalIgnoreCase), "a", "A", "b"]);
            }
        }
        """;
```

Add `[nameof(SampleSources.Csharp15Scenario)] = SampleSources.Csharp15Dsl,` to
`LoweringCoverageTests.DslFor`.

Create `test/Raun.Generator.Test/CSharp15BranchTests.cs`:

```csharp
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>C# 15 types branch with no help from Raun: the compiler's switch does the matching
/// (union "try both" semantics included) and its exhaustiveness check covers the cases.</summary>
public class CSharp15BranchTests
{
    private static GeneratorResult Generate() => GeneratorHarness.Run(SampleSources.Csharp15Dsl + SampleSources.Csharp15Scenario);

    private static ScenarioDefinition Scenario(GeneratorResult result, string method)
        => result.Definitions().Single(d => d.MethodName.EndsWith("." + method, StringComparison.Ordinal));

    [Fact]
    public async Task A_value_type_union_case_matches_and_binds_unboxed()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "Union").RunAsync();

        var noted = results.Where(r => r.Node.OperationName == "Note").ToList();
        Assert.Equal(StepStatus.Passed, noted.Single(r => r.Status == StepStatus.Passed).Status);
        Assert.Equal("code 42 noted", noted.Single(r => r.Status == StepStatus.Passed).DisplayName);
    }

    [Fact]
    public async Task A_closed_hierarchy_switch_runs_the_matching_case()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "Closed").RunAsync();

        Assert.Equal("closed for lunch noted", results.Single(r => r.Node.OperationName == "Note" && r.Status == StepStatus.Passed).DisplayName);
    }

    [Fact]
    public async Task A_collection_expression_argument_lowers_and_runs()
    {
        var result = Generate();
        result.AssertCompiles();

        var results = await Scenario(result, "CollectionArguments").RunAsync();

        Assert.Equal(StepStatus.Passed, results.Single(r => r.Node.OperationName == "Names").Status);
    }

    [Fact]
    public async Task The_csharp15_sample_is_clean()
        => Assert.Empty(await GeneratorHarness.DiagnoseAsync(SampleSources.Csharp15Dsl + SampleSources.Csharp15Scenario, requireCompilable: true));
}
```

`GeneratorHarness` parses with `LanguageVersion.Preview`, which includes C# 15. It references the
running framework's assemblies, which are net11.0 after Task 1, so `UnionAttribute`/`IUnion` resolve.

- [ ] **Step 2: Run them**

Run: `cd /c/dev/raun-issue2 && dotnet test test/Raun.Generator.Test --filter-class "*CSharp15BranchTests"`
Expected: PASS. If one fails:
- a parse error means the RC compiler is not being used, so recheck Task 1 Step 4;
- a lowering failure is a bug in Tasks 2-5: fix it there, with a test in that task's file.

- [ ] **Step 3: Add the sample scenario**

In `samples/AppointmentTests`, add a step returning a union and a scenario switching on it. Follow
the sample's own structure: its world, and its `Given`/`When`/`Then` step classes. Add a
`union BookingOutcome(Booked, Waitlisted, Refused)` and a step
`When.TryToBook(patient)` → `Task<BookingOutcome>`. Then add:

```csharp
public sealed class BookingOutcomeScenarios : ClinicScenarios   // the sample's scenario base
{
    [Scenario("a booking either lands, waits, or is refused")]
    public async Task BookingOutcome()
    {
        var patient = await Given.PatientExists("Ada");
        switch (await When.TryToBook(patient))
        {
            case Booked booked: await Then.AppointmentExists(booked.Appointment); break;
            case Waitlisted waitlisted: await Then.PatientIsOnWaitlist(patient, waitlisted.Position); break;
            case Refused refused: await Then.PatientWasTold(patient, refused.Reason); break;
        }
    }
}
```

Add a closed-hierarchy scenario the same way: a `closed record class` with two or three cases
returned by one new step, and a scenario switching on it with one `case` per subtype and no
`default` (the compiler checks exhaustiveness).

Adapt the names to the sample's real types and steps. Add only steps the scenarios need, and keep
them deterministic.

Run: `cd /c/dev/raun-issue2 && dotnet test samples/AppointmentTests`
Expected: PASS, with each new scenario's taken arm passing and the other arms not taken.

- [ ] **Step 4: Commit**

```bash
cd /c/dev/raun-issue2 && jj describe -m "test: union, closed-hierarchy and collection-argument scenarios

C# 15 types branch with nothing added to Raun: a union switch (a value-type
case included) and a closed-hierarchy switch run the matching arm, and a
collection expression with with(...) is an ordinary step argument. The
appointment sample gains a booking-outcome union switch." && jj new
```

---

### Task 7: Docs, spec refinements, release

**Files:**
- Modify: `README.md` ("Supported scenario subset", a new "Branching on a step's result" section
  after "Conditionals")
- Modify: `docs/RELEASING.md` (generator↔runtime contract; GA follow-up)
- Modify: `docs/superpowers/specs/2026-10-03-branching-and-net11-design.md` (the two refinements)

- [ ] **Step 1: README**

In "Supported scenario subset", replace the `if`/`else` bullet with:

```markdown
- A scenario branches on a step's result: `if (await S)` when the result is usable as a C# condition,
  `if (await S is <pattern>)`, or `switch (await S) { … }` with any patterns, `when` clauses and
  `default`. The condition is an ordinary step — discovered, timed, and reported like any other.
  Exactly one arm runs; the others are reported **not taken** (skipped, with the arm that ran as the
  reason), never green — and when no arm matches, every arm is not taken. A local assigned in some
  arms is merged automatically, and the statement after the branch waits for it to finish.
```

Change the loops bullet's `switch` mention to: `Loops (for/foreach/while/do), try/catch, and goto
are rejected`.

After "### Conditionals", add:

````markdown
### Branching on a step's result

```csharp
switch (await When.SubmitOrder(order))
{
    case Accepted { Total: > 1000 } big:
        await Then.ManualReviewRequested(big);
        break;
    case Accepted accepted when accepted.Express:
        await Then.ShipmentScheduled(accepted.Shipment);
        break;
    case Rejected rejected:
        await Then.CustomerNotified(rejected.Reason);
        break;
    default:
        await Then.OrderIsPending(order);
        break;
}
```

The patterns are C#'s own: Raun hands the switch to the compiler, so anything C# matches, a branch
matches. A pattern's variables (`big`, `accepted`, `rejected`) are step outputs inside their arm.
With C# 15, a `union` or a `closed` hierarchy as the step's result makes the compiler check that
every case is handled. A `when` clause follows the rules for step arguments; one that reads an
earlier step's result makes the decision wait for that step. A section ends in `break;`.
````

- [ ] **Step 2: RELEASING.md**

In "Generator ↔ runtime contract", add a first bullet:

```markdown
- 0.3.0 broke this contract once, on purpose: `ScenarioNode.EvaluateCondition` and
  `Guard.WhenValue` became `SelectArm`/`Arms` and `Guard.Arm`. Code generated by Raun 0.2 does not
  run on 0.3; regenerate (rebuild) instead. Scenario libraries are not supported (see below), so
  nothing generated outlives its package.
```

At the end of the file, add:

```markdown
## .NET 11 GA follow-up (November 2026)

0.3.x is built on the .NET 11 RC1 SDK. When .NET 11 is GA:

- `global.json`: the GA SDK version.
- `Directory.Packages.props`: `RaunTestRoslynVersion` becomes the GA `Microsoft.CodeAnalysis.CSharp`
  on nuget.org.
- `nuget.config`: remove the `dotnet-tools` source and its mapping.
- Both workflows: drop `dotnet-quality: preview`.
- `dotnet restore Raun.slnx` to refresh the lock files, then release.
```

- [ ] **Step 3: Record the spec refinements**

In the spec's "2. Runtime model":
- Change the `SelectArm` line in the code block to
  `public Func<IStepInputs, int>? SelectArm { get; init; }   // reads the value via inputs.Get<T>(Index)`.
- Add after the first bullet: "*(Refined in planning: the selector takes the inputs, because a
  `when` clause may read other step outputs.)*"

In "4. Platform", replace the "Generator baseline" bullet with:

```markdown
- **Generator baseline:** unchanged. Branching needs no new Roslyn API (the generator copies patterns
  into generated code; the consumer's compiler binds them), so the shipped generator carries no
  prerelease dependency. Only the **test projects** take RC1's compiler package — from Microsoft's
  `dotnet-tools` feed, source-mapped to `Microsoft.CodeAnalysis*` — so union and closed-hierarchy
  sources compile in tests. *(Refined in planning.)*
```

- [ ] **Step 4: Full verification**

Run: `cd /c/dev/raun-issue2 && dotnet build Raun.slnx -c Release 2>&1 | grep -E "warning|error" | grep -v MINVER`
Expected: no output.

Run: `cd /c/dev/raun-issue2 && dotnet test Raun.slnx -c Release 2>&1 | grep -E "^failed|failed:|succeeded:"`
Expected: `failed: 0`.

- [ ] **Step 5: Commit**

```bash
cd /c/dev/raun-issue2 && jj describe -m "docs: branching on a step's result; .NET 11 contract and GA follow-up" && jj new
```

- [ ] **Step 6: Land and release (ask Patrik before tagging)**

Move `main` to the last commit and push:

```bash
cd /c/dev/raun-issue2 && jj bookmark set main -r @- && jj git push --bookmark main
```

Wait for CI on that commit to go green. Use `gh run list --repo redoz/Raun --workflow CI --commit <sha>`,
then `gh run watch <id> --exit-status`.

**Confirm with Patrik** before tagging. Then tag from the colocated checkout:

```bash
cd /c/dev/punit && git tag -a v0.3.0-beta.1 <sha> -m "Raun 0.3.0 beta 1" && git push origin v0.3.0-beta.1 && jj git fetch
```

Watch the Release workflow, then check that nuget.org lists `0.3.0-beta.1` for `raun`, `raun.mtp`
and `raun.aspire`. Indexing can take a few minutes.
