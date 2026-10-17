using Raun.Reporting.Html;
using Xunit;

namespace Raun.Test;

public sealed class HtmlReportSourceOptionsTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine("source-link-fixture", "repo"));

    [Fact]
    public void The_report_links_methods_and_type_declarations_to_their_own_source_lines()
    {
        var options = new HtmlReportSourceOptions { BaseUrl = new Uri("https://github.com/org/repo/blob/abc123"), SourceRoot = Root };
        var builder = new HtmlReportModelBuilder(options);
        builder.OnScenarioStarted(new Raun.Model.ScenarioDefinition
        {
            ScenarioId = "source", DisplayName = "booking", MethodName = "Demo.Bookings.Book", Nodes = [],
            SourceFile = Path.Combine(Root, "tests", "Bookings.cs"), SourceLine = 42,
            TypeSourceFile = Path.Combine(Root, "tests", "Bookings.cs"), TypeSourceLine = 12,
        });
        var scenario = Assert.Single(builder.Build("now").Scenarios);
        Assert.Equal("https://github.com/org/repo/blob/abc123/tests/Bookings.cs#L42", scenario.SourceUrl);
        Assert.Equal("https://github.com/org/repo/blob/abc123/tests/Bookings.cs#L12", scenario.TypeSourceUrl);
    }

    [Fact]
    public void The_source_root_can_be_found_from_a_worktree_git_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "raun-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../metadata");
            var options = new HtmlReportSourceOptions { BaseUrl = new Uri("https://github.com/org/repo/blob/main") };
            Assert.Equal("https://github.com/org/repo/blob/main/tests/Test.cs#L7", options.GetUrl(Path.Combine(root, "tests", "Test.cs"), 7));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("https://github.com/org/repo/blob/abc123", "https://github.com/org/repo/blob/abc123/tests/Booking%20%23One.cs#L42")]
    [InlineData("https://git.example/org/repo/blob/main/", "https://git.example/org/repo/blob/main/tests/Booking%20%23One.cs#L42")]
    public void GitHub_links_keep_the_revision_and_encode_each_path_segment(string address, string expected)
    {
        var options = new HtmlReportSourceOptions { BaseUrl = new Uri(address, UriKind.RelativeOrAbsolute), SourceRoot = Root };
        Assert.Equal(expected, options.GetUrl(Path.Combine(Root, "tests", "Booking #One.cs"), 42));
    }

    [Fact]
    public void Azure_links_preserve_the_revision_and_select_the_source_line()
    {
        var options = new HtmlReportSourceOptions
        {
            BaseUrl = new Uri("https://dev.azure.com/org/project/_git/repo?version=GCabc123"), SourceRoot = Root,
        };
        Assert.Equal("https://dev.azure.com/org/project/_git/repo?version=GCabc123&path=%2Ftests%2FBooking%20%23One.cs&line=42&lineEnd=43&lineStartColumn=1&lineEndColumn=1&lineStyle=plain",
            options.GetUrl(Path.Combine(Root, "tests", "Booking #One.cs"), 42));
    }

    [Fact]
    public void Unknown_locations_and_files_outside_the_repository_do_not_get_links()
    {
        var options = new HtmlReportSourceOptions { BaseUrl = new Uri("https://github.com/org/repo/blob/main"), SourceRoot = Root };
        Assert.Null(options.GetUrl(null, 42));
        Assert.Null(options.GetUrl(Path.Combine(Root, "test.cs"), 0));
        Assert.Null(options.GetUrl(Path.Combine(Root, "..", "outside.cs"), 42));
        Assert.Null(options.GetUrl(Path.Combine(Root + "-other", "test.cs"), 42));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///repo/")]
    [InlineData("relative/path")]
    public void Source_links_require_an_absolute_web_url(string address)
    {
        var options = new HtmlReportSourceOptions { BaseUrl = new Uri(address, UriKind.RelativeOrAbsolute), SourceRoot = Root };
        Assert.Throws<ArgumentException>(() => new HtmlReportSink("report.html", TimeProvider.System, false, options));
    }
}
