using System.Globalization;
using Microsoft.CodeAnalysis;
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// A <c>[StepName]</c> placeholder may name a member path on a parameter — <c>{specification.Outcome}</c>
/// — resolved by symbol like a bare parameter is, rendered from the running value, and null-safe along
/// the way. A path that does not resolve is RAUN008, with the reason.
/// </summary>
public class PlaceholderPathTests
{
    private const string Calendars =
        """
        using System.Threading.Tasks;
        using Raun;

        namespace Calendars;

        public enum Outcome { Accepted, Rejected }
        public sealed record Employee(string Name);
        public sealed record Specification(Outcome Outcome, Employee? Employee);
        public readonly record struct Slot(int Hour);
        public sealed record Booking(System.DateTime When, System.DateOnly? Day, decimal Fee);

        public sealed class CalendarGiven : Given<NoWorld>
        {
            [StepName("Given an employee calendar with {specification.Outcome}")]
            public Task<Specification> Calendar(Specification specification) => Task.FromResult(specification);

            [StepName("Given {specification.Employee.Name}'s calendar")]
            public Task EmployeeCalendar(Specification specification) => Task.CompletedTask;

            [StepName("Given a slot at {slot.Hour}:00")]
            public Task SlotAt(Slot slot) => Task.CompletedTask;

            [StepName("Given {name.Length} letters")]
            public Task Letters(string name) => Task.CompletedTask;

            [StepName("Given a booking on {booking.When:yyyy-MM-dd} at {booking.When:HH:mm}, day {booking.Day:dd.MM}, fee {booking.Fee:0.00}")]
            public Task Booked(Booking booking) => Task.CompletedTask;

            [StepName("Given hour {hour:D2}")]
            public Task Hour(int hour) => Task.CompletedTask;
        }

        public sealed class CalendarThen : Then<NoWorld>
        {
            [StepName("Then {specification.Outcome} was recorded for {specification.Employee.Name}")]
            public Task Recorded(Specification specification) => Task.CompletedTask;
        }

        public abstract class CalendarSuite : Scenarios<NoWorld>
        {
            public CalendarGiven Given => Steps<CalendarGiven>();
            public CalendarThen Then => Steps<CalendarThen>();
        }

        public sealed class CalendarScenarios : CalendarSuite
        {
            [Scenario("calendars")]
            public async Task Calendars()
            {
                var specification = await Given.Calendar(new Specification(Outcome.Rejected, new Employee("Ada")));
                await Given.EmployeeCalendar(new Specification(Outcome.Accepted, null));
                await Given.SlotAt(new Slot(9));
                await Given.Letters("Jane");
                await Given.Booked(new Booking(new System.DateTime(2026, 9, 5, 9, 30, 0), null, 12.5m));
                await Given.Hour(9);
                await Then.Recorded(specification);
            }
        }
        """;

    [Fact]
    public async Task A_member_path_renders_the_running_value()
    {
        var result = GeneratorHarness.Run(Calendars);
        result.AssertCompiles();

        var results = await result.Definitions().Single().RunAsync();

        Assert.All(results, r => Assert.Equal(StepStatus.Passed, r.Status));
        Assert.Equal(
            [
                "Setup",
                "Given an employee calendar with Rejected",
                "Given 's calendar",                     // a null along the path renders as empty
                "Given a slot at 9:00",                  // a value-type receiver
                "Given 4 letters",
                "Given a booking on 2026-09-05 at 09:30, day , fee 12.50",   // a null formats as empty too
                "Given hour 09",
                "Then Rejected was recorded for Ada",    // a path through a prior step's result
                "Teardown",
            ],
            results.Select(r => r.DisplayName));
    }

    [Fact]
    public void A_member_path_is_left_for_run_time_in_the_discovered_name()
    {
        var result = GeneratorHarness.Run(Calendars);
        result.AssertCompiles();

        var calendar = result.Definitions().Single().Nodes.Single(n => n.OperationName == "Calendar");
        Assert.Equal("Given an employee calendar with {specification.Outcome}", calendar.DisplayNameTemplate);
        Assert.NotNull(calendar.FormatDisplayName);
    }

    [Fact]
    public void A_formatted_constant_is_folded_at_compile_time()
    {
        var result = GeneratorHarness.Run(Calendars);
        result.AssertCompiles();

        var hour = result.Definitions().Single().Nodes.Single(n => n.OperationName == "Hour");
        Assert.Equal("Given hour 09", hour.DisplayNameTemplate);
        Assert.Null(hour.FormatDisplayName);
    }

    [Fact]
    public async Task Valid_member_paths_produce_no_diagnostics()
        => Assert.Empty(await GeneratorHarness.DiagnoseAsync(Calendars, requireCompilable: true));

    [Theory]
    [InlineData("{specification.Missing}", "'Specification' has no readable instance member 'Missing'")]
    [InlineData("{specification.Outcome.Nope}", "'Outcome' has no readable instance member 'Nope'")]
    [InlineData("{specification.Secret}", "'Secret' is not readable from generated code")]
    [InlineData("{specification.Employee.ToString()}", "'ToString()' is not a member name")]
    [InlineData("{specification.}", "'' is not a member name")]
    [InlineData("{missing.Outcome}", "no parameter is named 'missing'")]
    [InlineData("{specification.Employee.Name:N2}", "'String' does not implement IFormattable, so it takes no format")]
    [InlineData("{specification.Outcome:}", "the format after ':' is empty")]
    public async Task RAUN008_says_why_a_path_does_not_resolve(string placeholder, string reason)
    {
        var source = Calendars.Replace(
            "public sealed record Specification(Outcome Outcome, Employee? Employee);",
            "public sealed record Specification(Outcome Outcome, Employee? Employee) { private int Secret => 0; }",
            StringComparison.Ordinal) +
            $$"""

            public sealed class BadThen : Then<NoWorld>
            {
                [StepName("Then {{placeholder}}")]
                public Task Bad(Specification specification) => Task.CompletedTask;
            }
            """;

        var diagnostics = await GeneratorHarness.DiagnoseAsync(source);

        var diagnostic = Assert.Single(diagnostics, d => d.Id == "RAUN008");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains(reason, diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }
}
