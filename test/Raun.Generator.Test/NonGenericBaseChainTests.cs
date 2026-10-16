using Xunit;

namespace Raun.Generator.Test;

public sealed class NonGenericBaseChainTests
{
    [Fact]
    public void Other_generic_arities_do_not_hide_the_framework_base()
    {
        const string source = """
            using System.Threading.Tasks;
            using Raun;
            namespace Raun
            {
                public abstract class Scenarios<TFirst, TSecond> : Scenarios;
                public abstract class Phase<TFirst, TSecond> : Given;
            }
            namespace MoreArity
            {
                public sealed class Checks : Phase<int, string>
                {
                    public Task Act() => Task.CompletedTask;
                }
                public sealed class Suite : Scenarios<int, string>
                {
                    public Checks Given => Steps<Checks>();
                    [Scenario] public async Task Run() { await Given.Act(); }
                }
            }
            """;
        var result = GeneratorHarness.Run(source);
        result.AssertCompiles();
        Assert.Single(result.Definitions());
    }
}
