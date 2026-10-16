using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raun;
using Xunit;

namespace AppointmentTests;

// Each scenario gets a fresh reception session; all its step classes share that session.
public sealed partial class ReceptionSession(ILogger<ReceptionSession> logger)
{
    public int CheckedInCount { get; private set; }

    public void CheckIn()
    {
        CheckedInCount++;
        PatientCheckedIn(logger, CheckedInCount);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reception checked in patient {Count}")]
    private static partial void PatientCheckedIn(ILogger logger, int count);
}

public sealed class ServiceScenarios : Scenarios<NoWorld>
{
    public ReceptionGiven Given => Steps<ReceptionGiven>();
    public ReceptionThen Then => Steps<ReceptionThen>();

    [Scenario("the first reception desk has its own session")]
    public async Task FirstReceptionDesk()
    {
        var session = await Given.PatientChecksIn();
        await Then.SameReceptionSession(session);
    }

    [Scenario("the second reception desk has its own session")]
    public async Task SecondReceptionDesk()
    {
        var session = await Given.PatientChecksIn();
        await Then.SameReceptionSession(session);
    }
}

public sealed class ReceptionGiven(ReceptionSession session) : Given<NoWorld>
{
    [StepName("Given a patient checks in at reception")]
    public Task<ReceptionSession> PatientChecksIn()
    {
        Assert.Equal(0, session.CheckedInCount);
        session.CheckIn();
        Assert.Contains(Context.LogEntries, entry => entry.Message.Contains("Reception checked in patient 1", StringComparison.Ordinal));
        Assert.Same(session, Context.Services!.GetRequiredService<ReceptionSession>());
        return Task.FromResult(session);
    }
}

public sealed class ReceptionThen(ReceptionSession session) : Then<NoWorld>
{
    [StepName("Then reception shares the session across its steps")]
    public Task SameReceptionSession(ReceptionSession checkedInSession)
    {
        Assert.Same(checkedInSession, session);
        Assert.Same(session, Context.Services!.GetRequiredService<ReceptionSession>());
        Assert.Equal(1, session.CheckedInCount);
        return Task.CompletedTask;
    }
}
