using Microsoft.Testing.Platform.Extensions.Messages;
using Raun.Model;
using Xunit;

namespace Raun.Mtp.Test;

public sealed class CaseDiscoverySteps : Given<NoWorld>
{
    [StepName("endpoint {endpoint}")]
    public Task Endpoint(string endpoint) => Task.CompletedTask;
}

public abstract class CaseDiscoverySuite : Scenarios<NoWorld>
{
    public CaseDiscoverySteps Given => Steps<CaseDiscoverySteps>();
}

public sealed class CaseDiscoveryScenarios : CaseDiscoverySuite
{
    [Scenario("authorization")]
    [ScenarioCase("anonymous", "/appointments")]
    [ScenarioCase("reader/50%", "/appointments")]
    public async Task Authorization(string endpoint) { await Given.Endpoint(endpoint); }
}

public class ScenarioCaseDiscoveryTests
{
    [Fact]
    public void Generated_cases_discover_distinct_uids_readable_groups_and_escaped_paths()
    {
        var definitions = ScenarioRegistry.Definitions()
            .Where(d => d.MethodName == "Raun.Mtp.Test.CaseDiscoveryScenarios.Authorization").ToList();
        Assert.Equal(["authorization [anonymous]", "authorization [reader/50%]"], definitions.Select(d => d.DisplayName));
        var discovered = definitions.SelectMany(d => RaunDiscoverer.BuildNodes(d)).ToList();
        Assert.NotEmpty(discovered);
        Assert.Equal(discovered.Count, discovered.Select(n => n.Uid.Value).Distinct().Count());
        foreach (var definition in definitions)
        {
            Assert.Equal("Authorization", definition.Method!.Name);
            foreach (var node in RaunDiscoverer.BuildNodes(definition))
            {
                Assert.Equal(definition.DisplayName, Assert.Single(node.Properties.OfType<TestMethodIdentifierProperty>()).MethodName);
            }
            foreach (var step in definition.Nodes)
            {
                var segments = ScenarioNodePath.For(definition, step).Split('/');
                Assert.Equal(6, segments.Length);
                Assert.Equal(definition.DisplayName.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal), segments[4]);
                Assert.StartsWith(definition.ScenarioId + ":", StepUid.Of(definition, step), StringComparison.Ordinal);
            }
        }
    }
}
