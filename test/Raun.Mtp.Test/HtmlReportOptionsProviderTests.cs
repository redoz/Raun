using Microsoft.Testing.Platform.Extensions.CommandLine;
using Xunit;

namespace Raun.Mtp.Test;

public class HtmlReportOptionsProviderTests
{
    [Theory]
    [InlineData("report-source-base-url", "https://github.com/org/repo/blob/main", true)]
    [InlineData("report-source-base-url", "javascript:alert(1)", false)]
    [InlineData("report-source-base-url", "relative", false)]
    [InlineData("report-source-root", "", false)]
    [InlineData("report-source-root", "/repo", true)]
    public async Task Source_options_validate_their_arguments(string name, string value, bool valid)
    {
        var provider = new HtmlReport.HtmlReportOptionsProvider();
        var option = provider.GetCommandLineOptions().Single(option => option.Name == name);
        Assert.Equal(ArgumentArity.ExactlyOne, option.Arity);
        Assert.Equal(valid, (await provider.ValidateOptionArgumentsAsync(option, [value])).IsValid);
    }

    [Fact]
    public void Registers_the_flag_and_filename_options()
    {
        var provider = new HtmlReport.HtmlReportOptionsProvider();
        var names = provider.GetCommandLineOptions().Select(o => o.Name).ToList();

        Assert.Contains("report-html", names);
        Assert.Contains("report-html-filename", names);
    }

    [Fact]
    public void The_flag_takes_no_argument_and_the_filename_takes_exactly_one()
    {
        var provider = new HtmlReport.HtmlReportOptionsProvider();
        var byName = provider.GetCommandLineOptions().ToDictionary(o => o.Name);

        Assert.Equal(ArgumentArity.Zero, byName["report-html"].Arity);
        Assert.Equal(ArgumentArity.ExactlyOne, byName["report-html-filename"].Arity);
    }

    [Fact]
    public async Task Filename_argument_must_be_non_empty()
    {
        var provider = new HtmlReport.HtmlReportOptionsProvider();
        var filename = provider.GetCommandLineOptions().Single(o => o.Name == "report-html-filename");

        var ok = await provider.ValidateOptionArgumentsAsync(filename, ["report.html"]);
        var bad = await provider.ValidateOptionArgumentsAsync(filename, [""]);

        Assert.True(ok.IsValid);
        Assert.False(bad.IsValid);
    }
}
