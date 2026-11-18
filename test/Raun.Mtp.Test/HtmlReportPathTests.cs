using Xunit;

namespace Raun.Mtp.Test;

public class HtmlReportPathTests
{
    private const string Net10 = ".NETCoreApp,Version=v10.0";
    private const string Net11 = ".NETCoreApp,Version=v11.0";

    [Fact]
    public void Returns_null_when_the_flag_is_absent()
        => Assert.Null(HtmlReport.HtmlReportPath.Resolve(enabled: false, filename: null, resultsDirectory: @"C:\r"));

    [Fact]
    public void The_default_filename_names_the_test_assembly_and_its_framework()
    {
        var path = HtmlReport.HtmlReportPath.Resolve(
            enabled: true, filename: null, resultsDirectory: @"C:\r", assemblyName: "Clinic.Tests", targetFrameworkName: Net10);

        Assert.Equal(Path.Combine(@"C:\r", "Clinic.Tests_net10.0.html"), path);
    }

    [Fact]
    public void One_project_built_for_two_frameworks_writes_two_reports_into_a_shared_results_directory()
    {
        var net10 = HtmlReport.HtmlReportPath.Resolve(true, null, @"C:\r", "Clinic.Tests", Net10);
        var net11 = HtmlReport.HtmlReportPath.Resolve(true, null, @"C:\r", "Clinic.Tests", Net11);

        Assert.NotEqual(net10, net11);
    }

    [Fact]
    public void Two_projects_write_two_reports_into_a_shared_results_directory()
    {
        var clinic = HtmlReport.HtmlReportPath.Resolve(true, null, @"C:\r", "Clinic.Tests", Net10);
        var billing = HtmlReport.HtmlReportPath.Resolve(true, null, @"C:\r", "Billing.Tests", Net10);

        Assert.NotEqual(clinic, billing);
    }

    [Fact]
    public void Honors_an_explicit_filename()
    {
        var path = HtmlReport.HtmlReportPath.Resolve(
            enabled: true, filename: "run.html", resultsDirectory: @"C:\r", assemblyName: "Clinic.Tests", targetFrameworkName: Net10);

        Assert.Equal(Path.Combine(@"C:\r", "run.html"), path);
    }

    [Fact]
    public void Falls_back_to_current_directory_when_results_directory_is_unknown()
    {
        var path = HtmlReport.HtmlReportPath.Resolve(
            enabled: true, filename: null, resultsDirectory: null, assemblyName: "Clinic.Tests", targetFrameworkName: Net10);

        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "Clinic.Tests_net10.0.html"), path);
    }

    [Theory]
    [InlineData(null, null, "raun-report.html")]
    [InlineData("Clinic.Tests", null, "Clinic.Tests.html")]
    [InlineData("Clinic.Tests", ".NETFramework,Version=v4.8", "Clinic.Tests.html")]
    [InlineData(null, Net10, "raun-report_net10.0.html")]
    public void An_unknown_assembly_or_framework_is_left_out_of_the_name(string? assembly, string? framework, string expected)
        => Assert.Equal(expected, HtmlReport.HtmlReportPath.DefaultFilename(assembly, framework));
}
