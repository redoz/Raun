using Raun;
using Xunit;

namespace AppointmentTests;

public sealed partial class AppointmentWhen : When
{
    [StepName("When recording approval of {patient}")]
    public async Task ManuallyApprovePatient([Read] Patient patient)
    {
        await Context.Resources.Apply(ResourceAction.For<ApprovedAttribute>(), patient);
        var approval = Assert.Single(Context.Resources.Effects, effect => effect.Action is not null);
        Assert.Equal("Approve", approval.Action!.Name);
        Assert.Equal(ResourceEffect.Update, approval.Effect);
        Assert.Equal(LockMode.Exclusive, approval.Mode);
        Assert.Equal(patient.Name, approval.Label);
    }
}

public sealed partial class AppointmentThen : Then
{
    [StepName("Then {patientId} is audited")]
    public Task AuditPatient([Audited, ResourceKey<Patient>] string patientId, string expectedName)
    {
        var audit = Assert.Single(Context.Resources.Effects);
        Assert.Equal("Audited", audit.Action!.Name);
        Assert.Equal(ResourceEffect.Read, audit.Effect);
        Assert.Equal(LockMode.Shared, audit.Mode);
        Assert.Equal(expectedName, audit.Label);
        Assert.Null(audit.Data);
        Assert.Null(audit.DataSnapshot);
        Assert.Equal($"Then {expectedName} is audited", Context.StepDisplayName);
        Context.Log($"Audited {new ResourceIdentity(typeof(Patient), patientId)}");
        return Task.CompletedTask;
    }
}

public sealed class ResourceActionScenarios : AppointmentSuite
{
    // Apply uses the same action metadata and access rules as the attribute on ApprovePatient.
    [Scenario("manual approvals keep patient names for later audits")]
    [ScenarioCase("morning desk", "Ada")]
    [ScenarioCase("afternoon desk", "Linnea")]
    public async Task ManualApproveAndAudit(string name)
    {
        var patient = await Given.PatientExists(name);
        await When.ManuallyApprovePatient(patient);
        await Then.AuditPatient(patient.Id, patient.Name);
    }
}
