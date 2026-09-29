using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// A LINQ-unrolled group substitutes the loop variable with a literal, so each element's display name
/// can be folded at compile time. Before this, `--list-tests` showed three identical "user {name}
/// exists" entries and the real names appeared only at run time.
/// </summary>
public class LinqDisplayNameTests
{
    [Fact]
    public void Unrolled_steps_get_their_substituted_names_at_discovery_time()
    {
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinqScenario);
        result.AssertCompiles();
        var def = Assert.Single(result.Definitions());

        // 0..2 are the unrolled UserExists steps for user-1, user-2, user-3.
        Assert.Equal("user user-1 exists", def.Nodes[1].DisplayNameTemplate);
        Assert.Equal("user user-2 exists", def.Nodes[2].DisplayNameTemplate);
        Assert.Equal("user user-3 exists", def.Nodes[3].DisplayNameTemplate);

        // Fully constant: no runtime formatter is emitted for them.
        Assert.Null(def.Nodes[1].FormatDisplayName);
        Assert.Null(def.Nodes[3].FormatDisplayName);
    }

    [Fact]
    public void A_hole_that_needs_evaluation_stays_runtime_formatted()
    {
        // `i * 2` is not a literal after substitution (`1 * 2`), so the name is still formatted at run
        // time — correctly, since the generator does not evaluate arithmetic.
        var result = GeneratorHarness.Run(SampleSources.Dsl +
            """

            public sealed class LinqMathScenarios : AppointmentSuite
            {
                [Scenario("linq math")]
                public async Task Import()
                {
                    var users = await Enumerable.Range(1, 2)
                        .Select(i => Given.UserExists($"user-{i * 2}"))
                        .ToArray();

                    var import = await When.ImportUsers(users);
                    await Then.ImportShouldContainUsers(import, users);
                }
            }
            """);
        result.AssertCompiles();
        var def = Assert.Single(result.Definitions());

        Assert.Equal("user {name} exists", def.Nodes[1].DisplayNameTemplate);
        Assert.NotNull(def.Nodes[1].FormatDisplayName);
    }
}
