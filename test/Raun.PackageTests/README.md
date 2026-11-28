# Package-consumption scenarios

Run this suite after packing all four Raun packages into `artifacts/`:

```sh
dotnet restore test/Raun.PackageTests/Raun.PackageTests.csproj --locked-mode
dotnet run --project test/Raun.PackageTests/Raun.PackageTests.csproj -c Release -- --report-html
```

Set `RAUN_PACKAGE_FEED` to use a different local feed. It must contain exactly one
version of each package, with all four versions matching.

The cases build and run the Appointment sample, the Aspire sample, and a small
app with a generated entry point. Each consumer sits outside the repository, uses
exact package versions and a fresh cache, and gets Raun packages only from the local
feed. External dependencies use the versions in the samples' lock files.

The suite builds every consumer twice, once at each end of the SDK range that Raun supports:

- **oldest**: the lowest installed .NET SDK that Raun supports, targeting `net10.0`. This is not the
  SDK the repository pins. A generator that the oldest supported compiler cannot load still builds
  on the newest SDK, so only the oldest SDK shows that failure.
- **newest**: the highest installed .NET SDK, targeting its own framework, `net11.0`. This
  consumer gets each package's newest assets. Its generated app adds scenarios that use C# 15
  unions as step results. It is the one place where the generator lowers a union inside the same
  compiler that a union user has. The samples are written for `net10.0`, and here the same sources
  and package versions are built for the newer target.

A further scenario reads the packages themselves. Every analyzer must reference a Roslyn version no
newer than its `roslynX.Y` folder names, and the `net10.0` dependency groups must stay on 10.x.

The suite itself builds from project references. Only its child projects use
the packages. It stays outside `Raun.slnx` because the packages must exist before it
runs. The CI and release workflows run it after packing and before publishing.

The report includes child-process output and consumer reports as attachments.
Successful runs delete their consumer reports and temporary directories during Teardown.
Failed runs keep them for inspection; their paths appear in the Given step's log.

The suite uses these features where they describe real work:

| Feature | Use in this suite |
| --- | --- |
| Inline cases | Both samples and the generated-entry-point app, each on the oldest and the newest SDK |
| Resources, labels, and references | Links from the feed to the consumer and from the consumer to the report, in the matrix |
| Custom resource actions | Restored and Built describe workspace changes |
| Notes and resource-aware logs | Explain isolation and link output to consumers |
| Branches | Aspire collects coverage; the other consumers use their normal runner |
| Parallel groups | Package resolution and report checks are independent reads |
| Pooled capacity | At most two SDK workloads run together |
| Timeouts and cancellation | Bound external commands and stop their process trees |
| Cleanup policies | Remove successful workspaces and keep failed ones |
| Coverage | Check classified execution evidence from the packaged coverage integration |
