# Mutation testing with Stryker.NET

`Booking` is a small library of booking rules. `Booking.Tests` tests it with Raun scenarios.
Stryker.NET mutates the library and runs the scenarios against every mutant.
Stryker's Microsoft.Testing.Platform runner is a preview feature of Stryker.NET.

Under Stryker, Raun reports one test per scenario instead of one per step. Stryker runs every covering test on
its own for each mutant, and selecting a step runs all the steps before it, so per-step tests would re-run
the earlier steps over and over. Raun detects Stryker through the `STRYKER_MUTANT_FILE` environment variable;
`RAUN_TEST_GRANULARITY=step|scenario` overrides that.

The sample runs Stryker twice, from this directory:

```sh
dotnet stryker -f stryker-config.json -O ../../artifacts/stryker/scenario
RAUN_TEST_GRANULARITY=step dotnet stryker -f stryker-config.json -O ../../artifacts/stryker/step
```

Then, from the repository root, check the reports:

```sh
node test/Stryker/check-report.cjs artifacts/stryker/scenario/reports/mutation-report.json artifacts/stryker/step/reports/mutation-report.json
```

The check requires that the first run lists scenario tests, that the second lists step tests for the same
scenarios, that both runs agree on every mutant, and that every mutant outside the sentinel line is killed.

## The sentinel

The one line in `Booking/BookingRules.cs` marked `// stryker-sentinel` is called by a step but never asserted on,
so its mutant must survive. A broken test host would fail every test and "kill" every mutant, which looks like a
perfect score. A surviving sentinel shows the tests ran and judged the code.
