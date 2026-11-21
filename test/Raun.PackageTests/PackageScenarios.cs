using Raun;
using Xunit;

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
    [ScenarioCase("generated entry point, oldest SDK", "GeneratedEntryPoint", "oldest")]
    [ScenarioCase("Appointment sample, oldest SDK", "AppointmentTests", "oldest")]
    [ScenarioCase("Aspire sample, oldest SDK", "AspireAppointments", "oldest")]
    [ScenarioCase("generated entry point, newest SDK", "GeneratedEntryPoint", "newest")]
    [ScenarioCase("Appointment sample, newest SDK", "AppointmentTests", "newest")]
    [ScenarioCase("Aspire sample, newest SDK", "AspireAppointments", "newest")]
    [Uses<SdkSlots>]
    [Teardown(Run.OnSuccess)]
    public async Task ConsumePackages(string sample, string baseline)
    {
        var feed = await Given.BuiltPackages().Note("All four packages must exist at the same version.");
        var consumer = await Given.IsolatedConsumer(feed, sample, baseline)
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

    [Scenario("built packages suit the oldest supported consumer")]
    public async Task PackagesSuitTheBaseline()
    {
        var feed = await Given.BuiltPackages();
        await (Then.AnalyzersLoadOnTheirRoslynBaseline(feed), Then.Net10DependenciesStayOn10(feed), Then.PackagesDependOnEachOtherExactly(feed));
    }
}

public sealed class PackageGiven : Given
{
    [StepName("Given the locally built {$return}")]
    [return: Read]
    public Task<PackageFeed> BuiltPackages() => Task.FromResult(PackageConsumer.FindFeed());

    [StepName("Given {sample} consumes {feed} in isolation on the {baseline} supported SDK")]
    [return: Isolated]
    public Task<PackageConsumer> IsolatedConsumer([Isolated.Feed] PackageFeed feed, string sample, string baseline)
    {
        var consumer = PackageConsumer.Prepare(feed, sample, PackageConsumer.BaselineFor(baseline), Context);
        Context.Log($"Created {consumer} from {feed} in {consumer.Root}, built with the .NET SDK {consumer.Baseline.Sdk} for {consumer.Baseline.Framework}");
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
    [return: Reported]
    public Task<ConsumerReport> Running([Reported.Consumer] PackageConsumer consumer)
        => consumer.RunWithReportAsync(Context, collectCoverage: false);

    [StepName("When running {consumer} with code coverage", TimeoutMs = 600000)]
    [return: Reported]
    public Task<ConsumerReport> RunningWithCoverage([Reported.Consumer] PackageConsumer consumer)
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
        if (report.Consumer.StartsWith("Generated entry point", StringComparison.Ordinal))
            Assert.Equal(PackageConsumer.GeneratedAppScenarios(report.Consumer), count);
        Context.Log($"{report} contains {count} passing scenarios.");
        return Task.CompletedTask;
    }

    [StepName("Then every analyzer in {feed} loads on the Roslyn its folder names")]
    public Task AnalyzersLoadOnTheirRoslynBaseline([Read] PackageFeed feed)
    {
        foreach (var line in PackageContents.VerifyAnalyzersMatchTheirRoslynFolder(feed.Directory))
        {
            Context.Log(line);
        }

        return Task.CompletedTask;
    }

    [StepName("Then the packages of {feed} depend on each other at exactly their own version")]
    public Task PackagesDependOnEachOtherExactly([Read] PackageFeed feed)
    {
        foreach (var line in PackageContents.VerifyRaunDependenciesAreExact(feed.Directory))
        {
            Context.Log(line);
        }

        return Task.CompletedTask;
    }

    [StepName("Then the net10.0 dependencies of {feed} stay on 10.x")]
    public Task Net10DependenciesStayOn10([Read] PackageFeed feed)
    {
        foreach (var line in PackageContents.VerifyNet10DependenciesStayOn10(feed.Directory))
        {
            Context.Log(line);
        }

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
