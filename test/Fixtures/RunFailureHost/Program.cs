using Microsoft.Extensions.DependencyInjection;
using Raun;
using Raun.Mtp;

namespace RunFailureHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // Not disposed here: the provider owns nothing but the scoped service, and that one is
        // disposed (and throws) with each scenario's scope.
#pragma warning disable CA2000
        var services = new ServiceCollection().AddScoped<Session>().BuildServiceProvider();
#pragma warning restore CA2000
        if (Environment.GetEnvironmentVariable("RAUN_FIXTURE_STALE_CONTRACT") == "1")
        {
            // What a referenced library built against another Raun does when its module loads.
            ScenarioRegistry.Register(
                ScenarioRegistry.ContractVersion + 1, "Stale.Library.Scenario", static () => throw new InvalidOperationException("stale code ran"));
        }

        return await RaunTestApplication.RunAsync(args, services: services).ConfigureAwait(false);
    }
}

/// <summary>A scenario-scoped service that cannot be closed: its disposal fails after every step passed.</summary>
public sealed class Session : IDisposable
{
#pragma warning disable CA1065 // A Dispose that throws is the fault this fixture exists to produce.
    public void Dispose() => throw new InvalidOperationException("the session could not be closed");
#pragma warning restore CA1065
}

public sealed class FixtureScenarios : Scenarios
{
    public FixtureGiven Given => Steps<FixtureGiven>();

    [Scenario("a scenario whose session cannot be closed")]
    public async Task SessionCannotBeClosed()
    {
        await Given.ASessionIsOpened();
    }

    [Scenario("a healthy scenario")]
    public async Task Healthy()
    {
        await Given.NothingSpecial();
    }
}

public sealed class FixtureGiven : Given
{
    [StepName("Given a session is opened")]
    public Task ASessionIsOpened()
    {
        Context.Services!.GetRequiredService<Session>();
        return Task.CompletedTask;
    }

    [StepName("Given nothing special")]
    public Task NothingSpecial() => Task.CompletedTask;
}
