using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Raun.PackageTests;

[System.ComponentModel.DisplayName("Consumer")]
public sealed partial class PackageConsumer : Raun.IResource<PackageConsumer>
{
    private static readonly HashSet<string> PackageIds = ["Raun", "Raun.Mtp", "Raun.Aspire", "Raun.Coverage"];

    private PackageConsumer(string root, string project, string version)
    {
        Root = root;
        Project = project;
        Version = version;
    }

    public string Root { get; }
    public string Project { get; }
    public string Version { get; }
    public string ReportDirectory => Path.Combine(Root, "report");

    public string Name => Path.GetFileNameWithoutExtension(Project) switch
    {
        "GeneratedEntryPoint" => "Generated entry point",
        "AppointmentTests" => "Appointment sample",
        "AspireAppointments.Tests" => "Aspire sample",
        var name => name,
    };
    public static Raun.ResourceKey KeyFor(PackageConsumer consumer) => consumer.Root;
    public static string LabelFor(PackageConsumer consumer) => consumer.Name;

    private static string FindRepository()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "Raun.slnx")))
            repo = repo.Parent ?? throw new InvalidOperationException("Cannot find repository root.");
        return repo.FullName;
    }

    public static PackageFeed FindFeed()
    {
        var feed = Path.GetFullPath(Environment.GetEnvironmentVariable("RAUN_PACKAGE_FEED") ?? Path.Combine(FindRepository(), "artifacts"));
        return new PackageFeed(feed, ReadPackageVersion(feed));
    }

    public static PackageConsumer Prepare(PackageFeed feed, string sample, Raun.ScenarioContext context)
    {
        var version = feed.Version;
        var root = Directory.CreateTempSubdirectory("raun-package-consumption-").FullName;
        var relative = sample == "AspireAppointments"
            ? "AspireAppointments/AspireAppointments.Tests/AspireAppointments.Tests.csproj"
            : $"{sample}/{sample}.csproj";
        var consumer = new PackageConsumer(root, Path.Combine(root, relative), version);
        context.OnTeardown(async teardown =>
        {
            // Only remove the exact temporary directory created above; keep failures for inspection.
            Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), Path.GetDirectoryName(root));
            Assert.StartsWith("raun-package-consumption-", Path.GetFileName(root), StringComparison.Ordinal);
            Directory.Delete(root, recursive: true);
            await teardown.Resources.Delete(consumer);
            teardown.Log($"Removed {consumer} and its temporary package cache.");
        });
        Configure(root, FindRepository(), feed.Directory);
        if (sample == "GeneratedEntryPoint")
            WriteGeneratedApp(root, version);
        else
            CopySample(root, FindRepository(), sample, version);
        new XDocument(new XElement("Solution", Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Select(project => new XElement("Project", new XAttribute("Path", Path.GetRelativePath(root, project).Replace('\\', '/'))))))
            .Save(Path.Combine(root, "Consumer.slnx"));
        return consumer;
    }

    private static string ReadPackageVersion(string feed)
    {
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(feed, "*.nupkg"))
        {
            using var archive = ZipFile.OpenRead(file);
            using var stream = archive.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
            var metadata = XDocument.Load(stream).Descendants().Single(e => e.Name.LocalName == "metadata");
            var id = metadata.Elements().Single(e => e.Name.LocalName == "id").Value;
            if (PackageIds.Contains(id))
                Assert.True(versions.TryAdd(id, metadata.Elements().Single(e => e.Name.LocalName == "version").Value), $"Duplicate package: {id}");
        }
        Assert.True(PackageIds.SetEquals(versions.Keys), "Expected all four Raun packages in the local feed.");
        return Assert.Single(versions.Values.Distinct(StringComparer.Ordinal));
    }

    private static void Configure(string root, string repo, string feed)
    {
        // These files stop directory searches before they can import repository build settings.
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), """
            <Project><PropertyGroup>
              <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
              <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
            </PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(root, "Directory.Build.targets"), "<Project />");
        File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project />");
        File.Copy(Path.Combine(repo, "global.json"), Path.Combine(root, "global.json"));
        new XDocument(new XElement("configuration",
            new XElement("packageSources", new XElement("clear"),
                new XElement("add", new XAttribute("key", "local"), new XAttribute("value", feed)),
                new XElement("add", new XAttribute("key", "nuget.org"), new XAttribute("value", "https://api.nuget.org/v3/index.json"))),
            new XElement("packageSourceMapping",
                new XElement("packageSource", new XAttribute("key", "local"),
                    PackageIds.Select(id => new XElement("package", new XAttribute("pattern", id)))),
                new XElement("packageSource", new XAttribute("key", "nuget.org"),
                    new XElement("package", new XAttribute("pattern", "*"))))))
            .Save(Path.Combine(root, "nuget.config"));
    }

    private static void CopySample(string root, string repo, string sample, string version)
    {
        var source = Path.Combine(repo, "samples", sample);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "artifacts")
                || Path.GetFileName(file).StartsWith("packages", StringComparison.Ordinal) && file.EndsWith(".lock.json", StringComparison.Ordinal))
                continue;
            var destination = Path.Combine(root, sample, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        foreach (var project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
        {
            var original = Path.Combine(repo, "samples", Path.GetRelativePath(root, project));
            var originalDirectory = Path.GetDirectoryName(original)!;
            var linuxLock = Path.Combine(originalDirectory, "packages.linux-x64.lock.json");
            var lockFile = OperatingSystem.IsLinux() && File.Exists(linuxLock)
                ? linuxLock : Path.Combine(originalDirectory, "packages.lock.json");
            using var dependencies = JsonDocument.Parse(File.ReadAllText(lockFile));
            var resolved = dependencies.RootElement.GetProperty("dependencies").GetProperty("net10.0");
            var document = XDocument.Load(project);
            foreach (var item in document.Root!.Elements().SelectMany(g => g.Elements()).ToArray())
            {
                switch (item.Name.LocalName)
                {
                    case "CompilerVisibleProperty" or "EnableMSTestRunner" or "TestingPlatformDotnetTestSupport"
                        or "GenerateProgramFile" or "GenerateTestingPlatformEntryPoint":
                        item.Remove();
                        break;
                    case "PackageReference":
                        item.SetAttributeValue("Version", $"[{resolved.GetProperty(item.Attribute("Include")!.Value).GetProperty("resolved").GetString()}]");
                        break;
                    case "ProjectReference":
                        var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, item.Attribute("Include")!.Value.Replace('\\', '/')));
                        var name = Path.GetFileNameWithoutExtension(target);
                        if (name.StartsWith("Raun", StringComparison.Ordinal))
                        {
                            if (name != "Raun.Generator")
                                item.AddBeforeSelf(new XElement("PackageReference", new XAttribute("Include", name), new XAttribute("Version", $"[{version}]")));
                            item.Remove();
                        }
                        else
                            Assert.False(Path.GetRelativePath(root, target).StartsWith("..", StringComparison.Ordinal), $"Reference outside isolated samples: {target}");
                        break;
                }
            }
            document.Save(project);
        }
    }

    private static void WriteGeneratedApp(string root, string version)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "GeneratedEntryPoint")).FullName;
        File.WriteAllText(Path.Combine(directory, "GeneratedEntryPoint.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
              <ItemGroup><PackageReference Include="Raun.Mtp" Version="[{{version}}]" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Scenarios.cs"), """
            using Raun;
            public sealed class PackageScenarios : Scenarios
            {
                public PackageGiven Given => Steps<PackageGiven>();
                [Scenario] public async Task Runs() { await Given.Passes(); }
            }
            public sealed class PackageGiven : Given
            {
                public Task Passes() => Task.CompletedTask;
            }
            """);
    }

    public async Task DotnetAsync(ScenarioContext context, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["NUGET_PACKAGES"] = Path.Combine(Root, ".packages");
        start.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Combine(Root, ".http-cache");
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            context.AddAttachment("stdout", await stdout);
            context.AddAttachment("stderr", await stderr);
        }
        context.Log($"{this}: dotnet {string.Join(' ', arguments)} exited {process.ExitCode}");
        Assert.True(process.ExitCode == 0, $"dotnet failed:{Environment.NewLine}{await stdout}{Environment.NewLine}{await stderr}");
    }

    public async Task<ConsumerReport> RunWithReportAsync(Raun.ScenarioContext context, bool collectCoverage)
    {
        List<string> arguments = ["run", "--project", Project, "-c", "Release", "--no-build", "--",
            "--report-html", "--results-directory", ReportDirectory];
        if (collectCoverage)
        {
            // The sample's own testconfig.json scopes the Microsoft collector to its assemblies and
            // turns static instrumentation off (github.com/microsoft/codecoverage/issues/203), so a
            // consumer gets working coverage on Linux without extra settings.
            arguments.AddRange(["--coverage", "--coverage-output-format", "xml"]);
        }
        await DotnetAsync(context, [.. arguments]);
        var report = new ConsumerReport(Path.Combine(ReportDirectory, "raun-report.html"), Name);
        context.OnTeardown(async teardown =>
        {
            File.Delete(report.File);
            await teardown.Resources.Delete(report);
            teardown.Log($"Removed {report}; its snapshot remains attached to the verification step.");
        });
        return report;
    }

    public static void VerifyCoverage(string html)
    {
        var match = ReportModel().Match(html);
        Assert.True(match.Success);
        using var model = JsonDocument.Parse(match.Groups[1].Value);
        var coverage = model.RootElement.GetProperty("coverage");
        Assert.True(coverage.GetProperty("status").GetString() == "available", coverage.GetRawText());
        Assert.NotEmpty(coverage.GetProperty("types").EnumerateArray());
        Assert.Contains(coverage.GetProperty("categories").EnumerateArray(), category =>
        {
            var counts = category.GetProperty("counts");
            return new[] { "exercised", "complete", "reached", "partial", "evaluated" }
                .Any(name => counts.GetProperty(name).GetInt32() > 0);
        });
    }

    public void VerifyAssets()
    {
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Project)!, "obj", "project.assets.json")));
        var libraries = assets.RootElement.GetProperty("libraries");
        Assert.True(libraries.TryGetProperty($"Raun/{Version}", out _), "The expected Raun package was not resolved.");
        foreach (var library in libraries.EnumerateObject())
        {
            var parts = library.Name.Split('/');
            if (!PackageIds.Contains(parts[0])) continue;
            Assert.Equal(Version, parts[1]);
            Assert.Equal("package", library.Value.GetProperty("type").GetString());
        }
    }

    public static int VerifyReport(string html)
    {
        var match = ReportModel().Match(html);
        Assert.True(match.Success, "The consumer did not produce a report model.");
        using var model = JsonDocument.Parse(match.Groups[1].Value);
        var scenarios = model.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.NotEmpty(scenarios);
        Assert.Contains(scenarios, scenario => scenario.GetProperty("steps").GetArrayLength() > 0);
        Assert.All(scenarios, scenario => Assert.Equal("passed", scenario.GetProperty("status").GetString()));
        return scenarios.Length;
    }

    [GeneratedRegex("<script id=\"model\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline, 1000)]
    private static partial Regex ReportModel();
}
