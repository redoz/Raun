using System.Linq;
using System.Text.Json;
using Raun;
using Raun.Model;
using Raun.Reporting.Html;
using Raun.Running;
using Raun.Testing;
using Xunit;

namespace Raun.Test;

public class HtmlReportModelBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static ScenarioNode Node(int index, string stepId, string phase, string template,
        int[]? dependsOn = null, string? group = null) => new()
    {
        Index = index, StepId = stepId, Phase = phase, OperationName = $"Op{index}",
        DisplayNameTemplate = template, DependsOn = dependsOn ?? [], GroupId = group,
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Def(params ScenarioNode[] nodes) => new()
    {
        ScenarioId = "scn", DisplayName = "customer books", MethodName = "Ns.Booking",
        ClassDisplayName = "Appointment booking", Nodes = nodes,
    };

    private static ScenarioDefinition Def(string id, params ScenarioNode[] nodes) => new()
    {
        ScenarioId = id, DisplayName = id, MethodName = "Ns." + id, Nodes = nodes,
    };

    private static StepResult Result(ScenarioNode node, DateTimeOffset startedAt, double ms,
        StepStatus status = StepStatus.Passed, IReadOnlyList<ResourceEffect>? effects = null,
        IReadOnlyList<string>? logs = null, IReadOnlyList<ResourceLineageRelation>? lineage = null,
        IReadOnlyDictionary<string, string>? attachments = null) => new()
    {
        Node = node, DisplayName = node.DisplayNameTemplate, Status = status,
        StartedAt = startedAt, Duration = TimeSpan.FromMilliseconds(ms),
        Effects = effects ?? [], Logs = logs ?? [], Lineage = lineage ?? [],
        Attachments = attachments ?? new Dictionary<string, string>(),
    };

    [Fact]
    public void Attachments_are_carried_per_step_ordered_by_name()
    {
        var n0 = Node(0, "r", "When", "When a travel reminder is sent");
        var def = Def(n0);

        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, attachments: new Dictionary<string, string>
        {
            ["reminder"] = "Hi Sven, plan your trip.",
            ["audit"] = "sent by admin",
        }));

        var step = Assert.Single(Assert.Single(builder.Build(generatedAtUtc: "2026-09-06T00:00:00Z").Scenarios).Steps);
        Assert.Equal(["audit", "reminder"], step.Attachments.Select(a => a.Name));
        Assert.Equal("Hi Sven, plan your trip.", step.Attachments[1].Value);
    }

    [Fact]
    public void Builds_the_expected_json_model()
    {
        var n0 = Node(0, "p", "Given", "Given patient Jane exists");
        var n1 = Node(1, "s", "Given", "Given an available slot exists");
        var n2 = Node(2, "c", "When", "When creating an appointment", dependsOn: [0, 1]);
        var def = Def(n0, n1, n2);

        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 40, effects:
        [
            new ResourceEffect
            {
                Verb = LifecycleVerb.Create, Identity = new ResourceIdentity(typeof(string), "Jane"),
                StepId = "p", Timestamp = T0.AddMilliseconds(1),
            },
        ]));
        builder.OnStepFinished(def, Result(n1, T0, 30));                 // concurrent with n0 → lane 1
        builder.OnStepFinished(def, Result(n2, T0.AddMilliseconds(40), 50,
            attachments: new Dictionary<string, string> { ["confirmation"] = "booked #1" }));

        var model = builder.Build(generatedAtUtc: "2026-06-09T12:00:01Z");
        var json = JsonSerializer.Serialize(model, JsonOptions);
        Snapshot.Verify(json);
    }

    [Fact]
    public void Overlapping_steps_are_packed_into_separate_lanes()
    {
        var n0 = Node(0, "a", "Given", "a");
        var n1 = Node(1, "b", "Given", "b");
        var def = Def(n0, n1);

        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 100));               // [0,100)
        builder.OnStepFinished(def, Result(n1, T0.AddMilliseconds(10), 50)); // [10,60) overlaps → lane 1

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Equal(0, scenario.Steps[0].Lane);
        Assert.Equal(1, scenario.Steps[1].Lane);
    }

    [Fact]
    public void Sequential_steps_reuse_lane_zero()
    {
        var n0 = Node(0, "a", "Given", "a");
        var n1 = Node(1, "b", "When", "b", dependsOn: [0]);
        var def = Def(n0, n1);

        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 50));                // [0,50)
        builder.OnStepFinished(def, Result(n1, T0.AddMilliseconds(50), 50)); // [50,100) no overlap → lane 0

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Equal(0, scenario.Steps[0].Lane);
        Assert.Equal(0, scenario.Steps[1].Lane);
    }

    [Fact]
    public void Resource_effects_roll_up_into_one_lifeline_per_identity()
    {
        var n0 = Node(0, "a", "Given", "a");
        var def = Def(n0);
        var id = new ResourceIdentity(typeof(string), "Jane");

        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, effects:
        [
            new ResourceEffect { Verb = LifecycleVerb.Create, Identity = id, StepId = "a", Timestamp = T0.AddMilliseconds(2) },
        ]));

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        var resource = Assert.Single(scenario.Resources);
        Assert.Equal("String", resource.Type);
        Assert.Equal("Jane", resource.Key);
        Assert.Equal("Create", Assert.Single(resource.Events).Verb);
    }

    [Fact]
    public void Recorded_relations_become_lineage_references()
    {
        var n0 = Node(0, "c", "When", "When creating an appointment");
        var def = Def(n0);
        var appointment = new ResourceIdentity(typeof(string), "appt-1");
        var patient = new ResourceIdentity(typeof(string), "Jane");
        var slot = new ResourceIdentity(typeof(int), "7");

        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, lineage:
        [
            new ResourceLineageRelation { Subject = appointment, Target = patient, Kind = LifecycleVerb.Reference },
            new ResourceLineageRelation { Subject = appointment, Target = slot, Kind = LifecycleVerb.Consume },
        ]));

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Equal(2, scenario.References.Count);

        var aggregation = scenario.References.Single(e => e.Kind == "Reference");
        Assert.Equal("String", aggregation.SubjectType);
        Assert.Equal("appt-1", aggregation.SubjectKey);
        Assert.Equal("String", aggregation.TargetType);
        Assert.Equal("Jane", aggregation.TargetKey);

        var composition = scenario.References.Single(e => e.Kind == "Consume");
        Assert.Equal("appt-1", composition.SubjectKey);
        Assert.Equal("Int32", composition.TargetType);
        Assert.Equal("7", composition.TargetKey);
    }

    [Fact]
    public void A_step_with_no_relations_yields_no_references()
    {
        var n0 = Node(0, "t", "Then", "Then the appointment should exist");
        var def = Def(n0);

        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, effects:
        [
            new ResourceEffect { Verb = LifecycleVerb.Reference, Identity = new ResourceIdentity(typeof(string), "Jane"), StepId = "t", Timestamp = T0.AddMilliseconds(1) },
        ]));

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Empty(scenario.References);
    }

    [Fact]
    public void Multiple_subjects_on_one_target_yield_multiple_relations()
    {
        var n0 = Node(0, "w", "When", "When transferring between accounts");
        var def = Def(n0);
        var from = new ResourceIdentity(typeof(string), "acc-from");
        var to = new ResourceIdentity(typeof(string), "acc-to");
        var bank = new ResourceIdentity(typeof(string), "Bank");

        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, lineage:
        [
            new ResourceLineageRelation { Subject = from, Target = bank, Kind = LifecycleVerb.Reference },
            new ResourceLineageRelation { Subject = to, Target = bank, Kind = LifecycleVerb.Reference },
        ]));

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Equal(2, scenario.References.Count);
        Assert.Contains(scenario.References, e => e.SubjectKey == "acc-from" && e.TargetKey == "Bank");
        Assert.Contains(scenario.References, e => e.SubjectKey == "acc-to" && e.TargetKey == "Bank");
    }

    [Fact]
    public void A_repeated_relation_is_deduped_across_steps()
    {
        var n0 = Node(0, "a", "When", "When step a");
        var n1 = Node(1, "b", "When", "When step b");
        var def = Def(n0, n1);
        var appointment = new ResourceIdentity(typeof(string), "appt-1");
        var patient = new ResourceIdentity(typeof(string), "Jane");

        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10, lineage:
            [new ResourceLineageRelation { Subject = appointment, Target = patient, Kind = LifecycleVerb.Reference }]));
        builder.OnStepFinished(def, Result(n1, T0, 10, lineage:
            [new ResourceLineageRelation { Subject = appointment, Target = patient, Kind = LifecycleVerb.Reference }]));

        var scenario = Assert.Single(builder.Build("x").Scenarios);
        Assert.Single(scenario.References);
    }

    [Fact]
    public void Scenarios_follow_the_RunStarted_order_whatever_order_they_started_and_finished_in()
    {
        var first = Def("first", Node(0, "a", "Given", "a"));
        var second = Def("second", Node(0, "b", "Given", "b"));

        var builder = new HtmlReportModelBuilder();
        builder.OnRunStarted([first, second]);
        builder.OnScenarioStarted(second);                         // admitted first
        builder.OnStepFinished(second, Result(second.Nodes[0], T0, 10));
        builder.OnScenarioStarted(first);
        builder.OnStepFinished(first, Result(first.Nodes[0], T0.AddMilliseconds(5), 10));

        var model = builder.Build("2026-09-06T00:00:00Z");
        Assert.Equal(["first", "second"], model.Scenarios.Select(s => s.ScenarioId));
    }

    [Fact]
    public void Interleaved_step_events_land_on_their_own_scenarios()
    {
        var a = Def("a", Node(0, "a0", "Given", "a0"), Node(1, "a1", "Then", "a1", dependsOn: [0]));
        var b = Def("b", Node(0, "b0", "Given", "b0"));

        var builder = new HtmlReportModelBuilder();
        builder.OnRunStarted([a, b]);
        builder.OnScenarioStarted(a);
        builder.OnScenarioStarted(b);
        builder.OnStepFinished(a, Result(a.Nodes[0], T0, 10));
        builder.OnStepFinished(b, Result(b.Nodes[0], T0, 10));
        builder.OnStepFinished(a, Result(a.Nodes[1], T0.AddMilliseconds(10), 10));

        var model = builder.Build("2026-09-06T00:00:00Z");
        Assert.Equal(2, model.Scenarios[0].Steps.Count);
        Assert.Single(model.Scenarios[1].Steps);
    }

    [Fact]
    public void Total_is_the_wall_span_across_overlapping_scenarios_not_their_sum()
    {
        var a = Def("a", Node(0, "a0", "Given", "a0"));
        var b = Def("b", Node(0, "b0", "Given", "b0"));

        var builder = new HtmlReportModelBuilder();
        builder.OnRunStarted([a, b]);
        builder.OnScenarioStarted(a);
        builder.OnScenarioStarted(b);
        builder.OnStepFinished(a, Result(a.Nodes[0], T0, 100));                      // 0..100
        builder.OnStepFinished(b, Result(b.Nodes[0], T0.AddMilliseconds(50), 100));  // 50..150

        Assert.Equal(150, builder.Build("2026-09-06T00:00:00Z").Summary.TotalMs);
    }

    [Fact]
    public void A_scenario_that_never_started_contributes_nothing_to_the_wall_span()
    {
        var a = Def("a", Node(0, "a0", "Given", "a0"));
        var never = Def("never", Node(0, "n0", "Given", "n0"));

        var builder = new HtmlReportModelBuilder();
        builder.OnRunStarted([a, never]);
        builder.OnScenarioStarted(a);
        builder.OnStepFinished(a, Result(a.Nodes[0], T0, 40));

        var model = builder.Build("2026-09-06T00:00:00Z");
        Assert.Equal(40, model.Summary.TotalMs);
        var scenario = Assert.Single(model.Scenarios);
        Assert.Equal("a", scenario.ScenarioId);
        Assert.Equal(1, model.Summary.Passed);
    }

    [Fact]
    public void An_empty_run_builds_an_empty_model()
    {
        var builder = new HtmlReportModelBuilder();
        builder.OnRunStarted([]);

        var model = builder.Build("2026-09-06T00:00:00Z");
        Assert.Empty(model.Scenarios);
        Assert.Equal(0, model.Summary.TotalMs);
        Assert.Equal(0, model.Summary.Passed);
        Assert.Equal(0, model.Summary.Failed);
        Assert.Equal(0, model.Summary.Skipped);
    }

    [Fact]
    public void Scenarios_skipped_by_a_failed_preflight_carry_no_time_and_leave_the_wall_span_to_what_ran()
    {
        // A failed preflight (the "raun" scenario) really ran for 40 ms; the two selected scenarios
        // were skip-published with StartedAt = default (see RunLoop.SkipScenarioAsync). Their
        // default timestamps must not become the wall-span origin.
        var preflight = Def("raun", Node(0, "pf", "Given", "Preflight"));
        var a = Def("a", Node(0, "a0", "Given", "a0"));
        var b = Def("b", Node(0, "b0", "Given", "b0"));

        var builder = new HtmlReportModelBuilder();
        builder.OnRunStarted([preflight, a, b]);
        builder.OnScenarioStarted(preflight);
        builder.OnStepFinished(preflight, Result(preflight.Nodes[0], T0, 40, StepStatus.Failed));
        builder.OnScenarioStarted(a);
        builder.OnStepFinished(a, Result(a.Nodes[0], default, 0, StepStatus.Skipped));
        builder.OnScenarioStarted(b);
        builder.OnStepFinished(b, Result(b.Nodes[0], default, 0, StepStatus.Skipped));

        var model = builder.Build("2026-09-06T00:00:00Z");
        Assert.Equal(40, model.Summary.TotalMs);
        Assert.Equal(["raun", "a", "b"], model.Scenarios.Select(s => s.ScenarioId));
        Assert.Equal(0, model.Scenarios[1].Steps[0].OffsetMs);
        Assert.Equal(0, model.Scenarios[1].DurationMs);
        Assert.Equal("skipped", model.Scenarios[1].Status);
    }

    [Fact]
    public void Uses_and_wait_are_carried_per_scenario()
    {
        var def = new ScenarioDefinition
        {
            ScenarioId = "w", DisplayName = "w", MethodName = "Ns.w", Nodes = [Node(0, "x", "Given", "x")],
            Uses = [new ContendedResourceUse(typeof(ExclusiveDb), LockMode.Exclusive), new ContendedResourceUse(typeof(SharedCatalog), LockMode.Shared)],
        };

        var builder = new HtmlReportModelBuilder();
        builder.OnRunStarted([def]);
        builder.OnScenarioStarted(def, waited: TimeSpan.FromMilliseconds(1234), waitedFor: typeof(ExclusiveDb));
        builder.OnStepFinished(def, Result(def.Nodes[0], T0, 10));

        var scenario = Assert.Single(builder.Build("2026-09-06T00:00:00Z").Scenarios);
        Assert.Equal(["ExclusiveDb:Exclusive", "SharedCatalog:Shared"], scenario.Uses);
        Assert.Equal(1234, scenario.WaitedMs);
        Assert.Equal("ExclusiveDb", scenario.WaitedFor);
    }

    [Fact]
    public void Step_labels_are_the_numbering_the_runner_shows_not_the_node_index()
    {
        // A standalone step is "1"; a parallel group shares "2" with sub-numbers. The MTP tree already
        // names steps this way, and the report must not call the same node something else.
        var n0 = Node(0, "a", "Given", "a");
        var n1 = Node(1, "b", "When", "b", dependsOn: [0], group: "g1");
        var n2 = Node(2, "c", "When", "c", dependsOn: [0], group: "g1");
        var def = Def(n0, n1, n2);

        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(def);
        builder.OnStepFinished(def, Result(n0, T0, 10));
        builder.OnStepFinished(def, Result(n1, T0.AddMilliseconds(10), 5));
        builder.OnStepFinished(def, Result(n2, T0.AddMilliseconds(10), 5));

        var steps = builder.Build("now").Scenarios.Single().Steps;

        Assert.Equal(["1", "2.1", "2.2"], steps.Select(s => s.Label));
    }
}
