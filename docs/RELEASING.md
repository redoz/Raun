# Releasing Raun

Versions come from git tags via [MinVer](https://github.com/adamralph/minver). There is no version
number in any file. Four packages ship from one tag, in lockstep: `Raun`, `Raun.Coverage`,
`Raun.Mtp`, and `Raun.Aspire`. `Raun` carries the source generator as an analyzer.

## Version scheme

- `v0.x.y` while pre-1.0. Anything may change between minor versions.
- An untagged commit builds as `<next patch>-preview.0.<height>`, for example `0.1.1-preview.0.7`.
  Pushes to `main` publish these previews to GitHub Packages, not nuget.org.
- A pre-release is a tag with a suffix: `v0.2.0-beta.1`. MinVer uses it verbatim.
  All `v*` tags, including pre-release tags, publish to nuget.org.

## Dependencies are locked

`RestorePackagesWithLockFile` is on repo-wide, so every project carries a `packages.lock.json` with
its fully resolved graph. That graph includes transitive packages and the samples' floating
`OpenTelemetry 1.*`. Both workflows restore in locked mode before building. A restore therefore
cannot pull a package nobody reviewed. Changing a reference means regenerating the lock files in
the same commit.

**Updating a package needs no second command.** Change a version from Visual Studio, from
`dotnet add package`, or by editing `Directory.Packages.props`, and the restore that follows
updates every lock file. `Directory.Build.props` turns `RestoreForceEvaluate` on outside locked
mode, and that setting lets a plain restore rewrite the lock files.

```bash
dotnet restore Raun.slnx                 # rewrites every lock file that moved
dotnet restore Raun.slnx --locked-mode   # what CI does; fails if a lock file is stale
```

One lock file per project describes every host. Aspire's Dashboard and Orchestration are not in
the lock files. Since Aspire 17 they come from the Aspire CLI bundle, not from per-platform
packages. The AppHost's build installs the bundle into the user's Aspire home, `~/.aspire`, when
it is missing.

`RestoreForceEvaluate` stays off in locked mode on purpose. Re-evaluating must not happen there,
so CI fails on a stale lock file instead of rewriting it.

Dependabot proposes NuGet and GitHub Actions updates weekly, grouped into one PR per ecosystem.
Workflow actions are pinned to commit SHAs with the tag in a trailing comment. Dependabot updates
both together.

## Checklist

1. `main` is green. `dotnet build Raun.slnx` reports 0 warnings and `dotnet test Raun.slnx` passes.
2. For a stable release, move the analyzer rules being released from
   `src/Raun.Generator/AnalyzerReleases.Unshipped.md` to `AnalyzerReleases.Shipped.md` under a
   `## Release X.Y.Z` heading, for RS2000 release tracking. Keep them unshipped for pre-releases.
   RS2007 does not accept a suffix such as `-beta.1` in a release header. Commit any move first.
3. Check the packages locally and look at the file names. They carry the version MinVer computed
   for the current commit. Portable PDBs are embedded in the packaged assemblies, the generator
   included, so pack produces no separate symbol packages.

   ```bash
   dotnet pack src/Raun/Raun.csproj -c Release -o artifacts
   dotnet pack src/Raun.Coverage/Raun.Coverage.csproj -c Release -o artifacts
   dotnet pack src/Raun.Mtp/Raun.Mtp.csproj -c Release -o artifacts
   dotnet pack src/Raun.Aspire/Raun.Aspire.csproj -c Release -o artifacts
   ```

   Run the [package-consumption scenarios](../test/Raun.PackageTests/README.md) after packing.
   They copy both samples outside the repo, replace framework project references with exact
   package versions, and restore into fresh caches. A third case checks the generated entry point.
   Both workflows run this suite before publishing and upload its HTML report, even when the suite
   fails.
4. Release from the GitHub UI. Open **Actions**, select **Release**, and choose **Run workflow** on
   `main`. Pick a bump and, for a pre-release, an optional label. Bumps work as in npm, and the
   workflow computes the next tag from the existing `v*` tags:

   | Bump | From `v0.3.0-beta.33` | From `v0.3.0` |
   | --- | --- | --- |
   | `prerelease` (default) | `v0.3.0-beta.34` | `v0.3.1-beta.1` |
   | `patch` | `v0.3.0` | `v0.3.1` |
   | `minor` | `v0.3.0` | `v0.4.0` |
   | `major` | `v1.0.0` | `v1.0.0` |
   | `prepatch` / `preminor` / `premajor` | `v0.3.1-beta.1` / `v0.4.0-beta.1` / `v1.0.0-beta.1` | same |

   Tick **Dry run** to see the version in the run summary without tagging. The workflow tags
   `main` locally, runs every test, and pushes the tag only after the tests pass, just before
   publishing. The workflow stops a stable bump while `AnalyzerReleases.Unshipped.md` still lists
   rules, as step 2 describes. `.github/scripts/next_version.py` computes the version, and
   `python3 .github/scripts/test_next_version.py` tests it.

   You can also tag by hand with git. jj imports tags but does not create them. Pushing the tag
   runs the same workflow:

   ```bash
   git tag -a v0.1.0-beta.1 -m "Raun 0.1.0 beta 1"
   git push origin v0.1.0-beta.1
   jj git fetch
   ```

5. The `Release` workflow builds, tests, packs with `ContinuousIntegrationBuild`, publishes all
   four packages to nuget.org using trusted publishing, and creates the GitHub release with
   the packages attached. Before the first tag:
   - In nuget.org, configure a trusted publishing policy for the package owner with GitHub
     repository owner `redoz`, repository `Raun`, and workflow file `release.yml`. Enter the file
     name only. Leave the environment empty because the workflow uses no GitHub environment.
     Grant the policy permission to publish new packages and new versions of all four package
     IDs. A `Raun.*` scope does not include the bare `Raun` package.
   - In GitHub Actions, set the repository **variable** `NUGET_USER` to the nuget.org account's
     username, not its email address. No persistent NuGet API key is needed.

   If the run fails after the workflow pushed the tag, for example because nuget.org rejected a
   package, fix the cause and use **Re-run failed jobs** on that same run. The publish job accepts
   a tag that already points at the release commit, pushes the packages nuget.org does not have
   yet, and creates or completes the GitHub release. The job pushes packages in dependency order:
   `Raun`, `Raun.Mtp`, `Raun.Coverage`, `Raun.Aspire`. A run that stops part way therefore never
   leaves a package whose dependencies are missing. Do not start a new run instead. A new run
   would compute the next version and leave the failed one half published.
6. Verify on nuget.org that `Raun`, `Raun.Coverage`, `Raun.Mtp`, and `Raun.Aspire` show the
   tagged version, the Apache-2.0 license, and the README.

## Where the packages live

- Tagged releases go to nuget.org, at `https://api.nuget.org/v3/index.json`. Consumers can
  install `Raun.Mtp` directly without GitHub Packages credentials.
- The CI workflow publishes a `0.x.y-preview.0.N` build to GitHub Packages for every push to
  `main`, so consumers can install the current head without a tag.
- GitHub Packages requires authentication even for public packages. To consume a preview, a
  consumer needs a `nuget.config` with the feed and a personal access token that has
  `read:packages`:

  ```xml
  <configuration>
    <packageSources>
      <add key="raun" value="https://nuget.pkg.github.com/redoz/index.json" />
    </packageSources>
    <packageSourceCredentials>
      <raun>
        <add key="Username" value="GITHUB_USERNAME" />
        <add key="ClearTextPassword" value="%GITHUB_PACKAGES_TOKEN%" />
      </raun>
    </packageSourceCredentials>
  </configuration>
  ```

  Then run `dotnet add package Raun.Mtp --prerelease` to install a preview.

## Consumer baseline

The `Raun` package carries the generator under `analyzers/dotnet/roslyn5.9/cs`. A consumer of
`Raun.Mtp` or `Raun.Aspire` gets the generator transitively, because those packages depend on
`Raun` with `PrivateAssets="none"`. Without that setting, pack would write the dependency with
`exclude="Build,Analyzers"`.

A consumer whose compiler is older than Roslyn 5.9 would get no generator and no diagnostics. Its
build would succeed and run zero tests. `buildTransitive/Raun.props` therefore fails such a build
up front with `RAUN018`. It compares `$(NETCoreSdkVersion)` against `$(RaunMinimumSdkVersion)`,
which is 10.0.400. Setting `RaunSkipSdkCheck=true` skips the check.
`test/Raun.Mtp.Test/SdkFloorTests.cs` runs the shipped props file against a range of versions.
The supported baseline today is the .NET 10 SDK 10.0.400 or later. Adding an older baseline means
adding a `Raun.Generator.RoslynNN` variant project, not a version bump. `Directory.Build.props`
describes the variant projects.

Two properties state the baseline, and they change together: `RaunRoslynVersion` in
`Directory.Build.props` and `RaunMinimumSdkVersion` in `buildTransitive/Raun.props`.
`RaunRoslynVersion` sets the `roslynX.Y` pack folder and, through `VersionOverride`, the Roslyn
version the generator compiles against. NuGet picks the analyzer folder by its name alone. A
compiler older than the Roslyn version the assembly references skips the generator with warning
CS9057, and the build still succeeds. The central `Microsoft.CodeAnalysis` versions in
`Directory.Packages.props` are not the generator's versions. `Raun.Coverage` and the generator
tests use them.

| .NET SDK | Roslyn |
| --- | --- |
| 10.0.1xx | 5.0 |
| 10.0.2xx | 5.3 |
| 10.0.3xx | 5.6 |
| 10.0.4xx | 5.9 |
| 11.0.100 | 5.12 |

The `net10.0` dependency groups stay on 10.x `Microsoft.Extensions.*` packages. An 11.x
dependency fails restore with NU1109 for a .NET 10 consumer that pins transitive packages. On
`net11.0` the same entries are pruned or act as a lower bound. Dependabot ignores major updates
of those packages and minor or major updates of Roslyn.

The package-consumption scenarios check all of this. They compare each packed analyzer's Roslyn
reference with its folder name and check the `net10.0` dependency groups. They build their
consumers with the lowest supported SDK installed, not the SDK that `global.json` pins. CI
installs 10.0.400 for this. They build every consumer a second time with the newest SDK
installed, targeting `net11.0`, so a real consumer also uses the `lib/net11.0` assets and C# 15
unions as step results.

The libraries ship `lib/net10.0` and `lib/net11.0`, set by `RaunTargetFrameworks` in
`Directory.Build.props`, and the tests run on both. The repository builds with the .NET 11 SDK
pinned in `global.json`. CI also installs the .NET 10 SDK for the `net10.0` test runs. The `Raun`
package gets the generator once, from `RaunPrimaryTargetFramework`. On `net11.0` the SDK prunes
the `Microsoft.Extensions.*.Abstractions` dependencies from the nuspec, because .NET 11 ships them
in the shared framework.

## Generator and runtime contract

Generated code calls into the runtime. It builds `ScenarioDefinition` and `ScenarioNode` values,
reads step inputs, opens scopes, formats titles, records resource actions and registers itself.
Everything the generated code names, sets or relies on in the runtime is the contract. The
contract has a number.

The generator and the runtime ship in one package, so within a single project they always match.
A referenced assembly can see a mismatch. Its scenarios were generated by the `Raun` version it
was built against, and the application that loads it may have resolved another version.

**The promise is refusal, not compatibility.** A runtime never runs code generated for a different
contract number:

- The generator writes its contract number into every registration:
  `ScenarioRegistry.Register(<contract>, "<key>", <factory>)`.
- The runtime keeps a registration only when the contract number matches its own
  `ScenarioRegistry.ContractVersion`. Otherwise the runtime drops the factory without calling it,
  and `ScenarioRegistry.Definitions()` throws `ScenarioContractException`, which names the
  assemblies to rebuild. Under Microsoft.Testing.Platform the exception shows as a failed
  `Raun run` node and a failing exit code.
- Before 1.0 the contract number may change in any release. From 1.0 it changes only in a major
  release.

Rules:

- Raise the contract number in `ScenarioEmitter.ContractVersion` and
  `ScenarioRegistry.ContractVersion` together whenever code generated before a change could not
  run correctly after it. Examples: a member the generated code uses was removed, renamed or
  retyped, a new member became `required`, or something the generated code relies on changed
  meaning. A test checks that the two numbers are equal.
- `ScenarioRegistry.Register(int, string, Func<ScenarioDefinition>)` never changes. The runtime
  uses it to recognise old code.
- `GeneratorContractTests.Contract_surface` is a snapshot of every runtime type the generated code
  names across the snapshot scenarios, with the contract number on its first line. When the
  snapshot changes, decide whether the contract number has to go up before you accept it. The
  snapshot records types and static members only. The author of a change still has to notice a
  changed signature or a changed meaning.
- The generated code uses these runtime members today:
  - It constructs `ScenarioDefinition`, `ScenarioNode`, `Guard`, `ContendedResourceUse` and
    `ScenarioTag` and sets their members.
  - `NodeKind`, `LockMode` and `Run`. The generated code casts an int to `Run`, so the ordinals of
    `Run` are frozen.
  - `IStepInputs.Get<T>` and `GetArgument<T>`.
  - `ScenarioScope.SetupAsync<T>` and `ScenarioScope<T>.Steps<T>()`.
  - `StepMessage`, `StepMessagePart` and `StepMessageBuilder`.
  - `ResourceAction.FromGenerated`, `ResourceKey`, `ResourceIdentity`, `ResourceMapping` and
    `IResourceMappingManifest`.
  - The `ResourceContext` verbs, which the emitter names by string: `Read`, `Load`, `Create`,
    `Edit`, `Delete`, `Reference`, `Consume`.
  - The operation names `Setup` and `Teardown`, and the phase string `Setup`.
  - The `ScenarioContext` title-capture helpers for stack-bound arguments.
- The generator emits the Microsoft.Testing.Platform entry point only when
  `Raun.Mtp.RaunTestApplication` resolves in the compilation. A project that references `Raun`
  alone, such as a scenario library or another host, therefore never gets a `Main` that cannot
  compile.
- A scenario library is still not supported. Registration is a `[ModuleInitializer]`, and the CLR
  loads a module only when code uses something in it. Scenarios in a referenced library therefore
  register only if the application uses a type from that library. Otherwise the run does not
  report them at all. Scenarios must live in the test executable, and the README says so too. A
  loader would change this. It could scan referenced assemblies, or the generator could emit a
  touch for each referenced Raun library. The contract number makes such a library safe to load.

Before the contract number existed, 0.3.0 made the changes below. Code generated by an earlier
Raun does not compile or run against 0.3.0, so rebuild it.

- `ScenarioNode.EvaluateCondition` and `Guard.WhenValue` became `SelectArm`/`Arms` and `Guard.Arm`.
  `Guard.NoArm`, which is -1, means "the condition matched no arm".
- A node states what it is with `ScenarioNode.Kind`. `IsSetup`, `IsTeardown` and `IsSynthetic` are
  read-only views of it. A `Merge` node and the `Teardown` node have no `Invoke`.
- A step result and a node carry one form of each message. On `StepResult`, `TitleMessage`,
  `NoteMessages` and `LogEntries` are what is set. `DisplayName`, `Notes` and `Logs` are read-only
  plain-text views. On `ScenarioNode`, `FormatDisplayName` and `FormatNotes` are gone.
- `ScenarioDefinition.Namespace` and `TypeName` are required, and nothing derives them from
  `MethodName`.
- Registrations state their contract number, starting at 1.

## Undoing a bad release

Packages cannot be deleted from nuget.org, only unlisted. Tag the fix as the next patch and unlist
the bad version in the nuget.org package settings.

## 0.3.0 beta 6 scope

Keep the coverage and structured-message APIs, including Task.Note, resource-aware logs
and StepName id/data formatting. The HTML template stays byte-for-byte identical to beta 4.
The experimental full-window coverage UI, header tabs and resource popup are deferred.
Structured notes and references and classified coverage remain in the result and report data for
API consumers. This beta does not promise their experimental HTML presentation.

## 0.3.0 beta 8 scope

The HTML report uses a shared Tests/Coverage workspace with matching header boxes,
indented scenario steps, status-colored step numbers and phase verbs. Notes stay visible.
Expanded steps show resource events before separately collapsed logs. Resource references
highlight their usage. Coverage keeps drilldown, band filtering and source snippets.
Report interaction checks run in CI and the release workflow before packing.

## 0.3.0 beta 9 scope

Step titles can reference a successful step's return value with `{$return:id ?? name}`. On
failure or a null return, titles keep the input snapshots and the fallback text. Resource types
use DisplayName. References keep their full JSON snapshots from recording time, with hover
inspection and Copy ID/Data. The report restores step start offsets, uses separate status
indicators and compact step numbers, and presents Setup as zero with muted lifecycle titles.
Hover inspection replaces the resource data expanders. The two scenario-spacing experiments were
reverted before release.
