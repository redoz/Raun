# Inline Scenario Cases Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Execute named inline constant cases as independently discoverable and runnable scenarios.

**Architecture:** Keep the standard Scenario marker and add repeatable ScenarioCase metadata. A shared semantic binder validates rows and substitutes typed constant expressions during normal graph lowering. Emit one ordinary definition per row, preserving the existing scheduler and execution lifetime.

**Tech Stack:** C# / .NET 10; Roslyn incremental generator and analyzer; xUnit v3; Microsoft.Testing.Platform.

**Spec:** ../specs/2026-10-16-inline-scenario-cases-design.md

## Global Constraints

- No generated attributes, DI argument factories, member-data providers, arrays or typeof values.
- Arguments are positional constants, enums or null; use implicit C# argument conversion rules and exact arity.
- Keys are nonblank, ordinal-case-sensitive and unique per method. Display is base name followed by ` [case key]`.
- Existing parameterless scenarios retain their generated source shape, scenario IDs, step IDs and method registry keys.
- Cases preserve source order within each method; identity never includes source row ordinal or argument values.
- Keep the five-segment MTP tree path and original authored MethodInfo metadata.
- Builds must have zero warnings; new diagnostic IDs go in AnalyzerReleases.Unshipped.md.
- Do not touch the uncommitted report work in the separate compact-report-flow worktree.

## Review Focus

1. A single bare null must bind as one nullable argument, not disappear as an empty params array. Task 1.
2. Enum flags, numeric narrowing within constant range, nullable conversions, quotes and non-finite floating constants must emit legal typed C#. Tasks 1 and 2.
3. Lambda parameters or member names matching a scenario parameter must not be substituted accidentally. Task 2.
4. Inserting or reordering rows must preserve existing case/step UIDs while changing only their presentation order. Task 2.
5. Two cases originating from the same method must both register, create separate worlds, and remain independently filterable. Tasks 2 and 3.

## Task 1: Standard case metadata and shared validation

**Files:**
- Create src/Raun/Attributes/ScenarioCaseAttribute.cs
- Create src/Raun.Generator/Lowering/ScenarioCaseBinding.cs
- Modify src/Raun.Generator/Analysis/ScenarioAnalyzer.cs
- Modify src/Raun.Generator/Diagnostics/Descriptors.cs
- Modify src/Raun.Generator/AnalyzerReleases.Unshipped.md
- Create test/Raun.Test/ScenarioCaseAttributeTests.cs
- Create test/Raun.Generator.Test/ScenarioCaseDiagnosticsTests.cs
- Update test/Raun.Generator.Test/StepArgumentDiagnosticsTests.cs only where the new missing-case diagnostic owns the error

**Interfaces:**
- Public ScenarioCaseAttribute(string caseName, params object?[] arguments), with read-only CaseName and Arguments.
- Internal ScenarioCaseBinding.Bind(SemanticModel model, IMethodSymbol method, MethodDeclarationSyntax syntax) returns ScenarioCaseBindingResult: IReadOnlyList<BoundScenarioCase> Cases and EquatableArray<ScenarioDiagnostic> Diagnostics. BoundScenarioCase has string Key, int SourceOrdinal and IReadOnlyDictionary<IParameterSymbol, ExpressionSyntax> Arguments. Keep this transient semantic binding out of long-lived incremental IR.
- Rows expose Key, SourceOrdinal and typed arguments in parameter order. Shared validation is consumed by both analyzer and generator.
- RAUN027 InvalidScenarioCase: invalid placement, missing rows, blank/duplicate names or arity.
- RAUN028 InvalidScenarioCaseArgument: unsupported value or incompatible parameter conversion.
- RAUN029 InvalidScenarioCaseSignature: generic/overloaded/optional/params/by-ref parameter forms in case-enabled methods.

- [ ] Add failing attribute metadata and analyzer tests for accepted string/enum/status rows and every validation rule in the spec. Assert diagnostic ID, source span and expected/actual type where relevant.
- [ ] Run the new tests and verify failure for missing API/binding behavior before production changes.
- [ ] Implement attribute and semantic binding. Normalize bare null from source argument syntax; reject explicit array argument lists. Validate parameters even when the authored body never reads them.
- [ ] Reuse Roslyn semantic conversion checks rather than maintaining a second numeric conversion table. Reject unsupported values with targeted diagnostics and no RAUN000/crashed-generator fallback.
- [ ] Wire the analyzer and Unshipped table; verify valid parameters are distinguished from the old unbound-parameter RAUN007 path. Update the existing test to preserve its span and error coverage under the more specific diagnostic.
- [ ] Build Release, run the new test classes using the built xUnit executable, and commit the tested metadata/validation slice.

## Task 2: Bind constants into independently generated scenarios

**Files:**
- Modify src/Raun.Generator/ScenarioGenerator.cs
- Modify src/Raun.Generator/Lowering/ScenarioParser.cs
- Modify src/Raun.Generator/Lowering/ArgumentLowering.cs
- Modify src/Raun.Generator/Lowering/Ir.cs
- Modify src/Raun.Generator/Emit/ScenarioEmitter.cs
- Modify src/Raun/ScenarioRegistry.cs
- Create test/Raun.Generator.Test/ScenarioCaseGenerationTests.cs
- Modify test/Raun.Generator.Test/GeneratorSnapshotTests.cs and add new case snapshots
- Modify test/Raun.Test/ScenarioRegistryTests.cs
- Extend test/Raun.Generator.Test/IncrementalCachingTests.cs for case-aware tracked-step incremental assertions

**Interfaces:**
- Consume the Task 1 binding result. Extend ScenarioParser.Lower with an optional row binding; its no-case call retains current behavior.
- ArgumentLowering receives the row's IParameterSymbol-to-ExpressionSyntax map and replaces only those symbols. Fully type expressions so step overload binding matches the authored parameter type.
- Add optional RegistrationKey to ParsedScenario and RegistryEntry; fall back to MethodFullName for legacy scenarios. Do not change the public ScenarioDefinition contract.
- Keep GeneratorSafety.SafeParse's singleton behavior; flatten one safely lowered ScenarioResult per valid row in the incremental pipeline.

- [ ] Add failing generation tests using GeneratorHarness.Run(source).Definitions(): assert two definitions with different constant inputs, unique builders/registry entries/IDs, original MethodInfo, and no generated warnings.
- [ ] Add tests for null and escaped strings, enum/flags and numeric boundary values; execute their generated steps and inspect captured typed argument values.
- [ ] Test use in a condition step, title placeholder and parallel child step. Test nested lambda shadowing to prove substitution is semantic rather than textual.
- [ ] Add reordering, inserted-row and edited-value variants: compare IDs by explicit case key, compare step IDs, and assert emitted CreateAll case order follows authored rows. Preserve every existing guard/merge assertion and legacy snapshot.
- [ ] Lower each valid row with bound constants. Generate safe member names using the existing method safe name plus the case ID, and hash method identity plus a separated explicit key for case identity. Use `base display [key]` for reporting.
- [ ] Give case registry keys a separate source-order component (ordinal plus explicit key) without using it in UIDs. Emit the distinct registry key while keeping MethodName and MethodInfo pointed at the original method.
- [ ] Make ScenarioRegistry.Definitions enumerate factories by ordinal registration-key order, matching its documented contract. Keep Register/TryGet singleton APIs and parameterless registration strings unchanged. Add a registry test with reverse insertion order proving deterministic enumeration and preservation of both cases.
- [ ] Review only the new received case snapshots; existing parameterless verified snapshots must remain unchanged. Run generator suite and registry tests, then commit the tested generation slice.

## Task 3: Execution, discovery, filtering and authorization example

**Files:**
- Create test/Raun.Generator.Test/ScenarioCaseExecutionTests.cs
- Create test/Raun.Mtp.Test/ScenarioCaseDiscoveryTests.cs
- Extend test/Raun.Mtp.Test/ScenarioNodePathTests.cs only for case path escaping
- Add samples/AppointmentTests/AuthorizationScenarios.cs using the sample's existing world/step-class authoring conventions
- Modify README.md to document the API, supported constants and stable keys

**Interfaces:**
- Consume ordinary ScenarioDefinitions generated by Task 2. Reuse the existing RunLoop, ScenarioScope, UID selectors and report sinks; add runtime special cases only if a regression test proves an existing integration assumption must change.
- The authorization example uses named anonymous/reader/admin cases over endpoint strings and a local deterministic request handler. It does not introduce a network service, DI argument factory, or external authentication dependency.

- [ ] Add failing integration tests that run two compiled cases through the existing execution harness: assert distinct world/scope instances, one Setup and Teardown per case, and independent success/failure results.
- [ ] Exercise MTP discovery for two cases from the same method: all step UIDs are unique and display grouping contains the readable case name. Verify a case name containing slash/percent keeps the five-segment path and existing escaping rules.
- [ ] Run a selected step UID in one case and assert predecessor closure plus teardown run only for that case, with no sibling world creation. Capture a report and assert its two case scenarios remain separate.
- [ ] Add the authorization sample and README example after generation works. Assert each inline expected status is checked and all configured cases appear in the sample report.
- [ ] Run locked restore, full Release build, generator tests, runtime tests, MTP tests and both existing report interaction scripts. Run the canonical sample; inspect case names and outcomes. Diagnose any failures rather than weakening assertions or blindly accepting snapshots.
- [ ] Request one fresh whole-branch review; fix important findings and rerun affected checks. Commit the verified execution/sample/docs slice. Do not push, tag or publish a release unless requested.

## Verification commands

Use the worktree C:/Users/redoz/.codex/worktrees/scenario-cases/punit throughout.

```powershell
dotnet restore Raun.slnx --locked-mode
dotnet build Raun.slnx -c Release --no-restore
dotnet test --project test/Raun.Generator.Test/Raun.Generator.Test.csproj -c Release --no-build
dotnet test --project test/Raun.Test/Raun.Test.csproj -c Release --no-build
dotnet test --project test/Raun.Mtp.Test/Raun.Mtp.Test.csproj -c Release --no-build
node --test test/Raun.Test/Reporting/message-view.test.cjs test/Raun.Test/Reporting/coverage-view.test.cjs
dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -c Release --no-build
```

For a focused class after building, use the generated test executable's xUnit option:
`test/Raun.Generator.Test/bin/Release/net10.0/Raun.Generator.Test.exe -class Raun.Generator.Test.ScenarioCaseDiagnosticsTests`.
Do not pass unsupported MTP --filter or --nologo flags.

## Self-review

The tasks cover the approved authoring API, all declared validation rules, stable identities,
parameterless compatibility, case source ordering, independent lifetimes, filtering and report
separation. Review Focus items have explicit tests in their owning tasks. The only ordering
clarification is enforcing the registry's already documented registration-key order; UIDs and
parameterless generated output remain compatible. No package dependency or version change is needed.
