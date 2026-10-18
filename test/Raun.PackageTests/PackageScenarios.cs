using Raun;

namespace Raun.PackageTests;

// Limit simultaneous SDK/coverage processes on smaller CI runners.
[PooledResource(2)]
public sealed class SdkSlots : IContendedResource;

public sealed class PackageScenarios : Scenarios
{
    public PackageGiven Given => Steps<PackageGiven>();
    public PackageWhen When => Steps<PackageWhen>();
    public PackageThen Then => Steps<PackageThen>();

    [Scenario("built packages work for consumers", Timeout = 900000)]
    [ScenarioCase("generated entry point", "GeneratedEntryPoint")]
    [ScenarioCase("Appointment sample", "AppointmentTests")]
    [ScenarioCase("Aspire sample", "AspireAppointments")]
    [Uses<SdkSlots>]
    [Teardown(Run.OnSuccess)]
    public async Task ConsumePackages(string sample)
    {
        var feed = await Given.BuiltPackages().Note("All four packages must exist at the same version.");
        var consumer = await Given.IsolatedConsumer(feed, sample)
            .Note($"Only {feed} supplies Raun packages; a fresh cache prevents an older build masking a broken package.");
        await When.Restoring(consumer);
        await When.Building(consumer);
        ConsumerReport report;
        if (await Given.SupportsCoverage(consumer))
        {
            report = await When.RunningWithCoverage(consumer)
                .Note("Exercise Raun.Coverage and its packaged build host, not just the runtime assemblies.");
            await Then.ContainsClassifiedCoverage(report);
        }
        else
        {
            report = await When.Running(consumer);
        }
        await (Then.ResolvesBuiltPackages(consumer), Then.ReportContainsPassingScenarios(report));
    }
}

public sealed class PackageGiven : Given
{
    [StepName("Given the locally built {$return}")]
    [return: Read]
    public Task<PackageFeed> BuiltPackages() => Task.FromResult(PackageConsumer.FindFeed());

    [StepName("Given {sample} consumes {feed} in isolation")]
    [return: Created(References = [nameof(feed)])]
    public Task<PackageConsumer> IsolatedConsumer([Read] PackageFeed feed, string sample)
    {
        var consumer = PackageConsumer.Prepare(feed, sample, Context);
        Context.Log($"Created {consumer} from {feed} in {consumer.Root}");
        return Task.FromResult(consumer);
    }

    [StepName("Given {consumer} supports coverage collection")]
    public Task<bool> SupportsCoverage([Read] PackageConsumer consumer)
        => Task.FromResult(Path.GetFileNameWithoutExtension(consumer.Project) == "AspireAppointments.Tests");
}

public sealed class PackageWhen : When
{
    [StepName("When restoring {consumer}", TimeoutMs = 600000)]
    public Task Restoring([Restored] PackageConsumer consumer)
        => consumer.DotnetAsync(Context, "restore", consumer.Project, "--configfile", Path.Combine(consumer.Root, "nuget.config"));

    [StepName("When building {consumer}", TimeoutMs = 600000)]
    public Task Building([Built] PackageConsumer consumer)
        => consumer.DotnetAsync(Context, "build", consumer.Project, "-c", "Release", "--no-restore", "-p:UseSharedCompilation=false", "-nr:false");

    [StepName("When running {consumer}", TimeoutMs = 600000)]
    [return: Created(References = [nameof(consumer)])]
    public Task<ConsumerReport> Running([Read] PackageConsumer consumer)
        => consumer.RunWithReportAsync(Context, collectCoverage: false);

    [StepName("When running {consumer} with code coverage", TimeoutMs = 600000)]
    [return: Created(References = [nameof(consumer)])]
    public Task<ConsumerReport> RunningWithCoverage([Read] PackageConsumer consumer)
        => consumer.RunWithReportAsync(Context, collectCoverage: true);
}

public sealed class PackageThen : Then
{
    [StepName("Then {consumer} resolved the exact built packages")]
    public Task ResolvesBuiltPackages([Read] PackageConsumer consumer)
    {
        consumer.VerifyAssets();
        Context.Log($"{consumer} resolved Raun {consumer.Version} from the local feed.");
        return Task.CompletedTask;
    }

    [StepName("Then {report} contains passing scenarios")]
    public Task ReportContainsPassingScenarios([Read] ConsumerReport report)
    {
        var html = File.ReadAllText(report.File);
        Context.AddAttachment("consumer-report.html", html);
        var count = PackageConsumer.VerifyReport(html);
        Context.Log($"{report} contains {count} passing scenarios.");
        return Task.CompletedTask;
    }

    [StepName("Then {report} contains classified code coverage")]
    public Task ContainsClassifiedCoverage([Read] ConsumerReport report)
    {
        var html = File.ReadAllText(report.File);
        Context.AddAttachment("consumer-report.html", html);
        PackageConsumer.VerifyCoverage(html);
        Context.Log($"{report} includes classified code coverage.");
        return Task.CompletedTask;
    }
}
