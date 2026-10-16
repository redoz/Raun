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
Temporary consumer directories are kept for inspection; their paths appear in the
Given step's log.
