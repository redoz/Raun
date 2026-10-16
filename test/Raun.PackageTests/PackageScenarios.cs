using Raun;
using Xunit;

namespace Raun.PackageTests;

public sealed class PackageScenarios : Scenarios
{
    public PackageGiven Given => Steps<PackageGiven>();
    public PackageWhen When => Steps<PackageWhen>();
    public PackageThen Then => Steps<PackageThen>();

    [Scenario("built packages work for consumers")]
    [ScenarioCase("generated entry point", "GeneratedEntryPoint")]
    [ScenarioCase("Appointment sample", "AppointmentTests")]
    [ScenarioCase("Aspire sample", "AspireAppointments")]
    public async Task ConsumePackages(string sample)
    {
        var consumer = await Given.IsolatedConsumer(sample);
        await When.Restoring(consumer);
        await When.Building(consumer);
        await Then.ResolvesBuiltPackages(consumer);
        await When.Running(consumer);
        await Then.ReportContainsPassingScenarios(consumer);
    }
}

public sealed class PackageGiven : Given
{
    [StepName("Given {sample} consumes the built packages in isolation")]
    public Task<PackageConsumer> IsolatedConsumer(string sample)
    {
        var consumer = PackageConsumer.Prepare(sample);
        Context.Log($"Testing Raun {consumer.Version} in {consumer.Root}");
        return Task.FromResult(consumer);
    }
}

public sealed class PackageWhen : When
{
    [StepName("When restoring from the local feed into a fresh cache")]
    public Task Restoring(PackageConsumer consumer)
        => consumer.DotnetAsync(Context, "restore", consumer.Project, "--configfile", Path.Combine(consumer.Root, "nuget.config"));

    [StepName("When building the package consumer")]
    public Task Building(PackageConsumer consumer)
        => consumer.DotnetAsync(Context, "build", consumer.Project, "-c", "Release", "--no-restore");

    [StepName("When running the package consumer")]
    public Task Running(PackageConsumer consumer)
        => consumer.DotnetAsync(Context, "run", "--project", consumer.Project, "-c", "Release", "--no-build", "--",
            "--report-html", "--results-directory", consumer.ReportDirectory);
}

public sealed class PackageThen : Then
{
    [StepName("Then the consumer resolved the exact built packages")]
    public Task ResolvesBuiltPackages(PackageConsumer consumer)
    {
        consumer.VerifyAssets();
        return Task.CompletedTask;
    }

    [StepName("Then its report contains passing scenarios")]
    public Task ReportContainsPassingScenarios(PackageConsumer consumer)
    {
        var html = File.ReadAllText(Path.Combine(consumer.ReportDirectory, "raun-report.html"));
        Context.AddAttachment("consumer-report.html", html);
        var count = PackageConsumer.VerifyReport(html);
        Assert.True(count > 0);
        Context.Log($"Verified {count} consumer scenarios.");
        return Task.CompletedTask;
    }
}
