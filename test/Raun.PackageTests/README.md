# Package-consumption scenarios

Run this suite after packing all four Raun packages into `artifacts/`:

```sh
dotnet restore test/Raun.PackageTests/Raun.PackageTests.csproj --locked-mode
dotnet run --project test/Raun.PackageTests/Raun.PackageTests.csproj -c Release -- --report-html
```

Set `RAUN_PACKAGE_FEED` to use a different local feed. It must contain exactly one
version of each package, with all four versions matching.

The three cases build and run the Appointment sample, the Aspire sample, and a small
app with a generated entry point. Each consumer lives outside the repository, uses
exact package versions and a fresh cache, and gets Raun packages only from the local
feed. External dependencies use the versions in the samples' lock files.

The suite uses project references to bootstrap itself; only its child projects use
the packages. It stays outside `Raun.slnx` because the packages must exist before it
runs. CI and release run it after packing and before publishing.

The report includes child-process output and consumer reports as attachments.
Successful runs delete their consumer reports and temporary directories during Teardown.
Failed runs keep them for inspection; their paths appear in the Given step's log.

The suite exercises features where they describe real work:

| Feature | Use in this suite |
| --- | --- |
| Inline cases | Both samples and the generated-entry-point app |
| Resources, labels, and references | Feed → consumer → report in the matrix |
| Custom resource actions | Restored and Built describe workspace changes |
| Notes and resource-aware logs | Explain isolation and link output to consumers |
| Branches | Aspire collects coverage; the other consumers use their normal runner |
| Parallel groups | Package resolution and report checks are independent reads |
| Pooled capacity | At most two SDK workloads run together |
| Timeouts and cancellation | Bound external commands and stop their process trees |
| Cleanup policies | Remove successful workspaces and keep failed ones |
| Coverage | Check classified execution evidence from the packaged coverage integration |
