using System.Globalization;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// An argument the lowering refuses is reported where it is written, with the reason — by the
/// analyzer as RAUN007 and by the generator as RAUN017, at the same span and for the same reason,
/// because both run the one argument lowering. The scenario is refused, never generated half-right.
/// </summary>
public class StepArgumentDiagnosticsTests
{
    [Theory]
    [InlineData("await Then.Equal(await Given.Isolation(1), null);", "await Given.Isolation(1)", "cannot await")]
    [InlineData("await Then.Equal(Given.Isolation(1), null);", "Given.Isolation(1)", "cannot run inside another step's argument")]
    [InlineData("await Then.Equal(setup.Existing[0] = \"x\", null);", "setup.Existing[0]", "must not change a step's result")]
    [InlineData("await Then.Equal(setup.Existing[0] += \"x\", null);", "setup.Existing[0]", "must not change a step's result")]
    [InlineData("await Then.Equal(System.Threading.Interlocked.Exchange(ref setup.Existing[0], \"x\"), null);", "setup.Existing[0]", "must not change a step's result")]
    [InlineData("await Then.Equal(new System.Collections.Generic.List<HiddenType>(), null);", "HiddenType", "not visible outside its type")]
    [InlineData("await Then.Satisfies<HiddenType>(null!, _ => true);", "HiddenType", "not visible outside its type")]
    [InlineData("await Then.Equal(Hidden, null);", "Hidden", "not visible outside its type")]
    [InlineData("await Then.Equal(HiddenConstant, null);", "HiddenConstant", "not visible outside its type")]
    [InlineData("await Then.Equal(RefusedScenarios.HiddenConstant, null);", "HiddenConstant", "not visible outside its type")]
    [InlineData("await Then.Equal(new HiddenType(), null);", "HiddenType", "not visible outside its type")]
    [InlineData("await Then.Equal(seed, null);", "seed", "parameter of the scenario method")]
    public async Task A_refused_argument_is_reported_by_both_at_the_same_span_for_the_same_reason(
        string statement, string offending, string reason)
    {
        var source = SampleSources.CompositionDsl + Scenario("static", "int seed", statement);

        var analyzed = Assert.Single(await GeneratorHarness.AnalyzeAsync(source, requireCompilable: true));
        var result = GeneratorHarness.Run(source);
        var generated = Assert.Single(result.GeneratorDiagnostics);

        Assert.Equal("RAUN007", analyzed.Id);
        Assert.Equal(offending, Text(source, analyzed));
        Assert.Contains(reason, Message(analyzed), StringComparison.Ordinal);

        Assert.Equal("RAUN017", generated.Id);
        Assert.Equal(analyzed.Location.SourceSpan, generated.Location.SourceSpan);
        Assert.Contains(Message(analyzed), Message(generated), StringComparison.Ordinal);
        Assert.DoesNotContain("RefusedScenarios", result.GeneratedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_instance_member_is_refused()
    {
        var source = SampleSources.CompositionDsl + Scenario("", "", "await Then.Equal(Seed, null);", instanceMembers: "public int Seed = 1;");

        var analyzed = Assert.Single(await GeneratorHarness.AnalyzeAsync(source, requireCompilable: true));

        Assert.Equal("RAUN007", analyzed.Id);
        Assert.Equal("Seed", Text(source, analyzed));
        Assert.Contains("instance member", Message(analyzed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_local_no_step_produced_is_refused_at_the_identifier()
    {
        // The declaration itself is RAUN002; the use is RAUN007 at the identifier, with the reason.
        var source = SampleSources.CompositionDsl + Scenario("static", "", "var n = 1; await Then.Equal(n, null);");

        var diagnostics = await GeneratorHarness.AnalyzeAsync(source);
        var invalid = Assert.Single(diagnostics, d => d.Id == "RAUN007");

        Assert.Equal("n", Text(source, invalid));
        Assert.Contains("a local no step produced", Message(invalid), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("await Then.Satisfies(setup, s => { var request = s.Request; return request is { Length: > 0 } r && r.Length > 0; });")]
    [InlineData("await Then.Satisfies(setup, s => { static bool Present<T>(T value) => value is not null; return Present<string>(s.Request); });")]
    [InlineData("await Then.Satisfies(setup, s => { Func<Task> later = async () => await Task.Yield(); return later is not null; });")]
    [InlineData("await Then.Satisfies(setup, s => s is { Customer.Name: not null });")]
    public async Task What_an_argument_declares_itself_is_its_own(string statement)
    {
        // Its locals, pattern variables, local functions, and the awaits inside its callbacks live and
        // run inside the lowered expression; none of them is a scenario-level name.
        var source = SampleSources.CompositionDsl + Scenario("static", "", statement);

        Assert.Empty(await GeneratorHarness.AnalyzeAsync(source, requireCompilable: true));
        GeneratorHarness.Run(source).AssertCompiles();
    }

    [Fact]
    public async Task A_local_assigned_in_an_arm_is_a_step_output_for_the_rest_of_that_arm()
    {
        // The parser binds `appointment` at the assignment; the analyzer must agree, or it reports a
        // RAUN007 for a scenario the generator lowers without complaint.
        var source = SampleSources.ConditionalDsl +
            """

            public static class ArmAssignmentScenarios
            {
                [Scenario("arm assignment")]
                public static async Task Run()
                {
                    var patient = await Given.PatientExists("Alice");
                    Appointment appointment;
                    if (await Given.IsPriority())
                    {
                        appointment = await When.CreateUrgent(patient);
                        await Then.AppointmentExists(appointment);
                    }
                    else
                    {
                        appointment = await When.CreateStandard(patient);
                    }

                    await Then.AppointmentExists(appointment);
                }
            }
            """;

        Assert.Empty(await GeneratorHarness.AnalyzeAsync(source, requireCompilable: true));
        GeneratorHarness.Run(source).AssertCompiles();
    }

    [Fact]
    public async Task An_awaited_step_assigned_to_anything_but_a_local_is_refused()
    {
        // The value would have nowhere to go: the generator has no slot for a field. Refused by both,
        // never lowered as a bare step that silently drops the write.
        var source = SampleSources.CompositionDsl + Scenario("static", "", "Store = await Given.Isolation(1);");

        var analyzed = Assert.Single(await GeneratorHarness.AnalyzeAsync(source, requireCompilable: true));
        var generated = Assert.Single(GeneratorHarness.Run(source).GeneratorDiagnostics);

        Assert.Equal("RAUN002", analyzed.Id);
        Assert.Equal("RAUN017", generated.Id);
    }

    [Fact]
    public async Task Scenario_class_helpers_and_explicit_type_arguments_resolve_in_generated_code()
    {
        var source = SampleSources.CompositionDsl + Scenario(
            "static",
            "",
            "await Then.Equal(Describe(customer), \"customer-7\"); await Then.Satisfies<Marker>(new Marker(\"m\"), m => m.State == \"m\");");

        Assert.Empty(await GeneratorHarness.AnalyzeAsync(source, requireCompilable: true));
        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();
        Assert.All(results.Where(r => !r.Node.IsTeardown), r => Assert.Equal(Raun.Model.StepStatus.Passed, r.Status));
    }

    [Fact]
    public void A_display_name_binds_named_arguments_by_name()
    {
        var source = SampleSources.CompositionDsl + Scenario("static", "", "await Then.Equal(expected: \"x\", actual: setup.Request);");

        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();

        var equal = result.Definitions().Single().Nodes.Single(n => n.OperationName == "Equal");
        Assert.Equal("{actual} equals x", equal.DisplayNameTemplate);
    }

    [Fact]
    public async Task An_unrolled_linq_body_is_checked_like_any_call()
    {
        var source = SampleSources.CompositionDsl + Scenario(
            "static",
            "",
            "await Enumerable.Range(1, 2).Select(i => Then.Equal(Hidden + i, null)).ToArray();");

        var analyzed = Assert.Single(await GeneratorHarness.AnalyzeAsync(source, requireCompilable: true));
        var generated = Assert.Single(GeneratorHarness.Run(source).GeneratorDiagnostics);

        Assert.Equal("Hidden", Text(source, analyzed));
        Assert.Equal(analyzed.Location.SourceSpan, generated.Location.SourceSpan);
    }

    private static string Scenario(string modifiers, string parameters, string statement, string instanceMembers = "")
        => $$"""

        namespace Composition.Tests
        {
            public sealed record Marker(string State);

            public {{modifiers}} class RefusedScenarios
            {
                private static readonly string Hidden = "hidden";
                private const string HiddenConstant = "constant";
                private sealed record HiddenType;
                internal static Isolation? Store;
                internal static string Describe(Customer customer) => customer.Name;
                {{instanceMembers}}

                [Scenario("refused")]
                public {{modifiers}} async Task Run({{parameters}})
                {
                    var isolation = await Given.Isolation(7);
                    var customer = await Given.Customer(isolation);
                    var employee = await Given.Employee(isolation);
                    var setup = await Given.CreationContext(isolation, new CreationSpec
                    {
                        Customer = customer,
                        Employee = employee,
                        Dossier = "d",
                    });
                    {{statement}}
                }
            }
        }
        """;

    private static string Text(string source, Diagnostic diagnostic)
        => source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    private static string Message(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);
}
