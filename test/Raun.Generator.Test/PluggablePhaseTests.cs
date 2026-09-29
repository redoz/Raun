using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>A step class deriving from <c>Phase&lt;TWorld&gt;</c> with its own <c>[PhaseName]</c> is a
/// custom phase: recognised exactly like the built-in Given/When/Then, its name the step's phase label.</summary>
public class PluggablePhaseTests
{
    private const string CustomPhaseSource =
        """
        using System.Threading.Tasks;
        using Raun;

        namespace Demo;

        public sealed record Widget(int Id);

        [PhaseName("Arrange")]
        public sealed class Arrangements : Phase<NoWorld>
        {
            [StepName("a widget exists")]
            public async Task<Widget> WidgetExists()
            {
                await Task.Yield();
                return new Widget(1);
            }
        }

        public sealed class CustomScenarios : Scenarios<NoWorld>
        {
            public Arrangements Arrange => Steps<Arrangements>();

            [Scenario("custom phase")]
            public async Task S()
            {
                await Arrange.WidgetExists();
            }
        }
        """;

    [Fact]
    public void A_custom_phase_is_recognised_and_names_the_phase()
    {
        var result = GeneratorHarness.Run(CustomPhaseSource);
        result.AssertCompiles();

        var def = Assert.Single(result.Definitions());
        // Every scenario also carries a Setup and a trailing Teardown node; the business step is the other one.
        var node = Assert.Single(def.Nodes, n => !n.IsTeardown && !n.IsSetup);
        Assert.Equal("Arrange", node.Phase);
        Assert.Equal("a widget exists", node.DisplayNameTemplate);
    }
}
