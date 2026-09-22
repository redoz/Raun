using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// A step's uid is what a filter names, what an IDE re-runs, and what a report links to. It used to
/// be hashed from the step's ordinal position, so inserting a step at the top of a scenario — or
/// wrapping one in an <c>if</c> — silently renamed every step after it: saved filters stopped
/// matching, and "re-run this test" in an IDE ran a different one. The uid is now hashed from what
/// the step IS (its operation and its arguments as written) plus its ordinal among identical
/// siblings, so only the steps that actually changed get new uids.
/// </summary>
public class StepIdStabilityTests
{
    private const string Baseline = """

        public static class BookingScenarios
        {
            [Scenario("booking")]
            public static async Task Booking()
            {
                var patient = await Given.PatientExists("Jane");
                var slot = await Given.AvailableSlot();
                var appointment = await When.CreateAppointment(patient, slot);
                await Then.AppointmentExists(appointment);
            }
        }
        """;

    private const string WithAnInsertedStep = """

        public static class BookingScenarios
        {
            [Scenario("booking")]
            public static async Task Booking()
            {
                var earlier = await Given.PatientExists("Inserted");
                var patient = await Given.PatientExists("Jane");
                var slot = await Given.AvailableSlot();
                var appointment = await When.CreateAppointment(patient, slot);
                await Then.AppointmentExists(appointment);
            }
        }
        """;

    private const string WithTwoIdenticalSteps = """

        public static class BookingScenarios
        {
            [Scenario("booking")]
            public static async Task Booking()
            {
                var patient = await Given.PatientExists("Jane");
                var twin = await Given.PatientExists("Jane");
                var slot = await Given.AvailableSlot();
                var appointment = await When.CreateAppointment(patient, slot);
                await Then.AppointmentExists(appointment);
            }
        }
        """;

    [Fact]
    public void Inserting_a_step_leaves_every_other_step_s_uid_alone()
    {
        var before = StepIds(Baseline);
        var after = StepIds(WithAnInsertedStep);

        Assert.Equal(before["an available slot exists"], after["an available slot exists"]);
        Assert.Equal(before["creating an appointment"], after["creating an appointment"]);
        Assert.Equal(before["the appointment should exist"], after["the appointment should exist"]);
    }

    [Fact]
    public void Wrapping_a_step_in_an_if_leaves_the_other_uids_alone()
    {
        var before = StepIds(SampleSources.ConditionalDsl + ConditionalBaseline, dsl: null);
        var after = StepIds(SampleSources.ConditionalDsl + ConditionalWrapped, dsl: null);

        Assert.Equal(before["patient Jane exists"], after["patient Jane exists"]);
        Assert.Equal(before["notifying the patient"], after["notifying the patient"]);
    }

    [Fact]
    public void Two_identical_steps_get_distinct_uids()
    {
        var ids = AllStepIds(WithTwoIdenticalSteps);

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_step_that_changes_its_arguments_gets_a_new_uid()
    {
        // The guard on the tests above: a uid that never moves would pass them all.
        var before = StepIds(Baseline);
        var after = StepIds(Baseline.Replace("\"Jane\"", "\"Janet\"", StringComparison.Ordinal));

        Assert.NotEqual(before["patient Jane exists"], after["patient Janet exists"]);
    }

    private const string ConditionalBaseline = """

        public static class BookingScenarios
        {
            [Scenario("booking")]
            public static async Task Booking()
            {
                var patient = await Given.PatientExists("Jane");
                await When.Notify(patient);
            }
        }
        """;

    private const string ConditionalWrapped = """

        public static class BookingScenarios
        {
            [Scenario("booking")]
            public static async Task Booking()
            {
                var patient = await Given.PatientExists("Jane");
                if (await Given.IsPriority())
                {
                    await When.Notify(patient);
                }
            }
        }
        """;

    private static IReadOnlyList<string> AllStepIds(string scenario, string? dsl = SampleSources.Dsl)
    {
        var result = GeneratorHarness.Run((dsl ?? "") + scenario);
        result.AssertCompiles();
        var definition = Assert.Single(result.Definitions());
        return [.. definition.Nodes.Where(n => !n.IsSynthetic && !n.IsTeardown).Select(n => n.StepId)];
    }

    private static Dictionary<string, string> StepIds(string scenario, string? dsl = SampleSources.Dsl)
    {
        var result = GeneratorHarness.Run((dsl ?? "") + scenario);
        result.AssertCompiles();
        var definition = Assert.Single(result.Definitions());
        return definition.Nodes
            .Where(n => !n.IsSynthetic && !n.IsTeardown)
            .ToDictionary(n => n.DisplayNameTemplate, n => n.StepId, StringComparer.Ordinal);
    }
}
