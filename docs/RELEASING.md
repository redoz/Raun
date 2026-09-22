# Releasing Raun

Versions come from git tags via [MinVer](https://github.com/adamralph/minver). There is no version
number in any file. Three packages ship from one tag, in lockstep: `Raun` (which carries the source
generator as an analyzer), `Raun.Mtp`, and `Raun.Aspire`.

## Version scheme

- `v0.x.y` while pre-1.0: anything may change between minors.
- An untagged commit builds as `<next patch>-preview.0.<height>`, e.g. `0.1.1-preview.0.7`. Fine
  for local packing; never published.
- A pre-release is a tag with a suffix: `v0.2.0-beta.1`. MinVer uses it verbatim.

## Checklist

1. `main` is green: `dotnet build Raun.slnx` (0 warnings) and `dotnet test Raun.slnx`.
2. Move the analyzer rules being released from `src/Raun.Generator/AnalyzerReleases.Unshipped.md`
   to `AnalyzerReleases.Shipped.md` under a `## Release X.Y.Z` heading (RS2000 release tracking).
   Commit that first.
3. Check the packages locally and look at the file names — they carry the version MinVer computed
   for the current commit:

   ```bash
   dotnet pack src/Raun/Raun.csproj -c Release -o artifacts
   dotnet pack src/Raun.Mtp/Raun.Mtp.csproj -c Release -o artifacts
   dotnet pack src/Raun.Aspire/Raun.Aspire.csproj -c Release -o artifacts
   ```

   To try them as a consumer would, point a scratch project's `nuget.config` at `artifacts/` (with
   `<clear/>` and nuget.org as the second source) and reference `Raun.Mtp` with version `*-*`.
4. Tag with git — jj imports tags but does not create them:

   ```bash
   git tag -a v0.1.0 -m "Raun 0.1.0"
   git push origin v0.1.0
   jj git fetch
   ```

5. The `Release` workflow builds, tests, packs with `ContinuousIntegrationBuild`, pushes to the
   repository's **GitHub Packages** feed, and creates the GitHub release with the packages attached.
   It authenticates with the workflow's own `GITHUB_TOKEN`; no secret to set up.
6. Verify under the repository's Packages tab that all three packages show the version, the license
   (Apache-2.0), and the README.

## Where the packages live (for now)

- **Feed:** `https://nuget.pkg.github.com/redoz/index.json` (GitHub Packages).
- **Previews:** every push to `main` publishes its `0.x.y-preview.0.N` build via the CI workflow, so
  the current head is always consumable without tagging.
- **Consuming:** GitHub Packages requires authentication even for public packages. A consumer needs
  a `nuget.config` with the feed and a personal access token that has `read:packages`:

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

  Then `dotnet add package Raun.Mtp --prerelease`.
- **Moving to nuget.org later:** in both workflows change `--source` to
  `https://api.nuget.org/v3/index.json` and `--api-key` to a `NUGET_API_KEY` secret. Versions and
  tags stay exactly as they are.

## Consumer baseline

The generator is packed in `Raun` under `analyzers/dotnet/roslyn5.3/cs`, and reaches a consumer of
`Raun.Mtp` or `Raun.Aspire` transitively because those packages depend on `Raun` with
`PrivateAssets="none"` (pack would otherwise write the dependency with `exclude="Build,Analyzers"`).
A consumer whose compiler is older than Roslyn 5.3 would get no generator and no diagnostics,
silently — a build that succeeds and runs zero tests. `buildTransitive/Raun.props` therefore fails
such a build up front with `RAUN018`, comparing `$(NETCoreSdkVersion)` against
`$(RaunMinimumSdkVersion)` (10.0.300); `RaunSkipSdkCheck=true` is the escape hatch, and
`test/Raun.Mtp.Test/SdkFloorTests.cs` drives the shipped props file over a range of versions.
Supported baseline today: the .NET 10 SDK 10.0.300 or later. Adding an older baseline means a
`Raun.Generator.RoslynNN` variant project (see `Directory.Build.props`), not a version bump.

## Generator ↔ runtime contract

The generated code targets only data: `ScenarioDefinition` / `ScenarioNode` object initializers,
`Guard`, `ContendedResourceUse`, `IStepInputs.Get<T>`, `ScenarioRegistry.Register`, and the
`ResourceContext` verbs by name. The generator and the runtime travel in one package, so a single
project never sees skew between them. A scenario *library* does: its generated code is compiled
against the `Raun` it referenced, and an application that consumes the library may resolve a newer
`Raun`. Older generated code against a newer runtime must therefore keep working. Rules:

- Never add a `required` member to `ScenarioDefinition` or `ScenarioNode`; new members are
  init-only with a default, and whatever reads them tolerates the default (`Namespace` and
  `TypeName` are the precedent — the adapters fall back to splitting `MethodName`).
- Never rename a `ResourceContext` verb the emitter names by string (`Read`, `Load`, `Create`,
  `Edit`, `Delete`, `Reference`, `Consume`).
- `Run`'s ordinals are frozen: the emitter writes an int cast.
- The generator emits the MTP entry point only when `Raun.Mtp.RaunTestApplication` resolves in the
  compilation, so `Raun` alone (a scenario library, another host) never produces a `Main` that
  cannot compile.
- A scenario library does not actually *run* today, and the rules above are about staying ready for
  one, not about supporting it: registration is a `[ModuleInitializer]`, and the CLR loads a module
  only when something touches it, so scenarios in a referenced library never register and the run
  reports zero tests, green. Scenarios must live in the test executable; README says so too. A
  loader (scanning referenced assemblies, or an emitted touch per referenced Raun library) is the
  work that would change this.

## Undoing a bad release

Packages cannot be deleted from nuget.org, only unlisted. Tag the fix as the next patch and unlist
the bad version in the nuget.org package settings.
