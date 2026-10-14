using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

public class NullableResourceCaptureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nullable_resource_arguments_compile_cleanly_and_keep_runtime_null_validation(bool missing)
    {
        var source = $$"""
            using System.Threading.Tasks;
            using Raun;
            public sealed record Patient(string Id) : IResource<Patient>
            {
                public static ResourceKey KeyFor(Patient patient) => patient.Id;
            }
            public static class Calls { public static int Read; }
            public sealed class GivenSteps : Given<NoWorld>
            {
                public Task<Patient?> Seed() => Task.FromResult<Patient?>({{(missing ? "null" : "new Patient(\"1\")")}});
                [StepName("read {patient}")]
                public Task Read([Read] Patient? patient) { Calls.Read++; return Task.CompletedTask; }
            }
            public sealed class Stories : Scenarios<NoWorld>
            {
                public GivenSteps Given => Steps<GivenSteps>();
                [Scenario] public async Task Story() { var patient = await Given.Seed(); await Given.Read(patient); }
            }
            """;
        var generated = GeneratorHarness.Run(source);
        generated.AssertCompiles();
        Assert.Empty(GeneratorHarness.GeneratedCodeWarnings(source));
        var results = await Assert.Single(generated.Definitions()).RunAsync();
        var read = Assert.Single(results, result => result.Node.OperationName == "Read");
        Assert.Equal(missing ? StepStatus.Failed : StepStatus.Passed, read.Status);
        var calls = generated.Assembly!.GetType("Calls")!.GetField("Read")!.GetValue(null);
        Assert.Equal(missing ? 0 : 1, calls);
        if (missing)
        {
            Assert.IsType<ArgumentNullException>(read.Exception);
            Assert.Empty(read.Effects);
        }
        else
        {
            Assert.Equal("1", Assert.Single(read.Effects).Identity.Key.Value);
            Assert.Equal("read Patient:1", read.DisplayName);
        }
    }
}
