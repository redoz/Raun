using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Model;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// A step's tree-filter path is /assembly/namespace/class/scenario/step. Segments are minimally
/// escaped rather than URL-encoded: a display name's spaces stay literal (so a name copied verbatim
/// out of --list-tests works as a filter), and only / and % are escaped, since a display name may
/// contain a slash.
/// </summary>
public class ScenarioNodePathTests
{
    private static ScenarioNode Node(string template, string phase = "Given") => new()
    {
        Index = 0,
        StepId = "s0",
        Phase = phase,
        OperationName = "Op",
        DisplayNameTemplate = template,
        DependsOn = [],
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static ScenarioDefinition Definition(
        string methodName, string displayName, string? classDisplayName, ScenarioNode step) => new()
    {
        ScenarioId = "scn",
        DisplayName = displayName,
        MethodName = methodName,
        ClassDisplayName = classDisplayName,
        Nodes = [step],
    };

    [Fact]
    public void The_path_has_five_segments_and_starts_with_a_slash()
    {
        var step = Node("patient Jane exists");
        var definition = Definition("Demo.Booking.CustomerBooks", "customer books", null, step);

        var path = ScenarioNodePath.For(definition, step);

        Assert.StartsWith("/", path, StringComparison.Ordinal);
        var segments = path.Split('/');
        Assert.Equal(6, segments.Length);          // leading empty + five segments
        Assert.Equal(string.Empty, segments[0]);
        Assert.Equal("Demo", segments[2]);
        Assert.Equal("Booking", segments[3]);
        Assert.Equal("customer books", segments[4]);
        Assert.Equal("patient Jane exists", segments[5]);
    }

    [Fact]
    public void A_class_display_name_overrides_the_derived_type()
    {
        var step = Node("a step");
        var definition = Definition("Demo.Booking.CustomerBooks", "customer books", "Appointment booking", step);

        Assert.Equal("Appointment booking", ScenarioNodePath.For(definition, step).Split('/')[3]);
    }

    [Fact]
    public void A_scenario_in_the_global_namespace_still_has_five_segments()
    {
        var step = Node("a step");
        var definition = Definition("Booking.CustomerBooks", "customer books", null, step);

        var segments = ScenarioNodePath.For(definition, step).Split('/');
        Assert.Equal(6, segments.Length);
        Assert.Equal(string.Empty, segments[2]);   // empty namespace segment, never a missing one
        Assert.Equal("Booking", segments[3]);
    }

    [Fact]
    public void A_slash_in_a_display_name_is_encoded_rather_than_splitting_the_path()
    {
        var step = Node("reads a/b");
        var definition = Definition("Demo.Booking.CustomerBooks", "customer books", null, step);

        var path = ScenarioNodePath.For(definition, step);

        Assert.Equal(6, path.Split('/').Length);
        Assert.Contains("a%2Fb", path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_percent_in_a_display_name_is_escaped_rather_than_corrupting_the_segment_count()
    {
        var step = Node("discount is 50% off");
        var definition = Definition("Demo.Booking.CustomerBooks", "customer books", null, step);

        var path = ScenarioNodePath.For(definition, step);

        Assert.Equal(6, path.Split('/').Length);
        Assert.Contains("50%25 off", path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_display_name_with_spaces_appears_verbatim_so_a_name_copied_from_list_tests_can_be_pasted_into_a_filter()
    {
        var step = Node("Given user alice exists");
        var definition = Definition("Demo.Booking.CustomerBooks", "bulk user import", null, step);

        var path = ScenarioNodePath.For(definition, step);
        var segments = path.Split('/');

        Assert.Equal("bulk user import", segments[4]);
        Assert.Equal("Given user alice exists", segments[5]);
    }

    [Fact]
    public void Properties_carry_the_phase_and_the_scenario_name()
    {
        var step = Node("a step", phase: "When");
        var definition = Definition("Demo.Booking.CustomerBooks", "customer books", null, step);

        var bag = ScenarioNodePath.Properties(definition, step);
        var metadata = bag.OfType<TestMetadataProperty>().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        Assert.Equal("When", metadata["Phase"]);
        Assert.Equal("customer books", metadata["Scenario"]);
    }
}
