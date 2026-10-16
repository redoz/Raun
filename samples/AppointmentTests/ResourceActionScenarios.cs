using System.ComponentModel;
using Raun;

namespace AppointmentTests;

[DisplayName("Approve")]
public sealed class ApprovedAttribute : EditedAttribute;
public sealed class AuditedAttribute : ReadAttribute;

public sealed partial class AppointmentWhen : When<NoWorld>
{
    [StepName("When approving {patient}")]
    public Task ApprovePatient([Approved] Patient patient) => Task.CompletedTask;
}

public sealed partial class AppointmentThen : Then<NoWorld>
{
    [StepName("Then {patientId} is audited")]
    public Task AuditPatient([Audited, ResourceKey<Patient>] string patientId)
    {
        Context.Log($"Audited {new ResourceIdentity(typeof(Patient), patientId)}");
        return Task.CompletedTask;
    }
}

public sealed class ResourceActionScenarios : AppointmentSuite
{
    [Scenario("resource actions reuse patient names")]
    public async Task ApproveAndAudit()
    {
        var patient = await Given.PatientExists("Jane <Doe>");
        await When.ApprovePatient(patient);
        await Then.AuditPatient(patient.Name);
    }
}
