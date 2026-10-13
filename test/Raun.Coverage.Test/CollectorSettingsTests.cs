using System.Text;
using Raun.Coverage.Projects;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class CollectorSettingsTests
{
    [Theory]
    [InlineData("{\"Configuration\":{\"IncludeTestAssembly\":true,\"CodeCoverage\":{\"ModulePaths\":{\"Exclude\":[\"Hidden\"]},\"Attributes\":{\"mergeDefaults\":false,\"Exclude\":[]}}}}")]
    [InlineData("<Configuration><IncludeTestAssembly>true</IncludeTestAssembly><CodeCoverage><ModulePaths><Exclude><ModulePath>Hidden</ModulePath></Exclude></ModulePaths><Attributes mergeDefaults='false'><Exclude /></Attributes></CodeCoverage></Configuration>")]
    public void Reads_existing_settings_formats_and_default_merging(string settings)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(settings));
        var scope = CollectorSettingsReader.Read(stream);
        Assert.True(scope.IncludeTestAssembly);
        Assert.False(scope.IncludesModule("Hidden.dll"));
        Assert.True(scope.IncludesModule("Visible.dll"));
        Assert.False(scope.MergeDefaultAttributes);
    }

    [Fact]
    public void Reads_effective_mtp_configuration_and_checks_any_matching_attribute()
    {
        var values = new Dictionary<string, string>
        {
            ["codeCoverage:Configuration:IncludeTestAssembly"] = "true",
            ["codeCoverage:Configuration:CodeCoverage:Sources:Exclude:0"] = ".*Secret.*",
        };
        var scope = CollectorSettingsReader.FromConfiguration(key => values.GetValueOrDefault(key));
        Assert.True(scope.IncludeTestAssembly);
        Assert.False(scope.IncludesSource("Secret.cs"));
        var compilation = TestCompilation.Create("""
            using System;
            [Obsolete, CLSCompliant(true)] class C { public void M() {} }
            """);
        var symbol = compilation.GetTypeByMetadataName("C")!;
        Assert.False(new CollectorScope { AttributeIncludes = ["CLSCompliant"], MergeDefaultAttributes = false }.IsExcluded(symbol));
    }
}
