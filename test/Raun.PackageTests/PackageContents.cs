using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Raun.PackageTests;

/// <summary>
/// Checks on what the packages contain, as opposed to whether one consumer happens to build. Both
/// rules guard a failure a consumer on the .NET 10 SDK meets and a build on the repository's own
/// SDK cannot: an analyzer the older compiler refuses to load, and a dependency the older target
/// framework should not be asked for.
/// </summary>
public static partial class PackageContents
{
    /// <summary>
    /// Every analyzer under <c>analyzers/dotnet/roslynX.Y</c> must reference Microsoft.CodeAnalysis
    /// X.Y or lower. NuGet picks the folder by name alone; a compiler older than the assembly's real
    /// reference then skips it with warning CS9057 and generates nothing. Returns what it checked.
    /// </summary>
    public static IReadOnlyList<string> VerifyAnalyzersMatchTheirRoslynFolder(string feed)
    {
        var checkedAssemblies = new List<string>();
        foreach (var package in Directory.EnumerateFiles(feed, "*.nupkg"))
        {
            using var archive = ZipFile.OpenRead(package);
            foreach (var entry in archive.Entries)
            {
                var match = RoslynFolder().Match(entry.FullName);
                if (!match.Success)
                {
                    continue;
                }

                var declared = new Version(
                    int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
                foreach (var (name, version) in RoslynReferences(entry))
                {
                    var referenced = new Version(version.Major, version.Minor);
                    Assert.True(
                        referenced <= declared,
                        $"{Path.GetFileName(package)}: {entry.FullName} references {name} {version}, but its folder "
                        + $"promises a compiler with Roslyn {declared} can load it. Such a compiler skips the analyzer (CS9057).");
                    checkedAssemblies.Add($"{entry.FullName} references {name} {referenced} (folder {declared})");
                }
            }
        }

        Assert.NotEmpty(checkedAssemblies);
        return checkedAssemblies;
    }

    /// <summary>
    /// A package's <c>net10.0</c> dependency group must not demand a Microsoft.Extensions.* or
    /// System.* package newer than 10.x: a consumer on .NET 10 that pins its transitive packages
    /// fails restore with NU1109. Returns what it checked.
    /// </summary>
    public static IReadOnlyList<string> VerifyNet10DependenciesStayOn10(string feed)
    {
        var checkedDependencies = new List<string>();
        foreach (var package in Directory.EnumerateFiles(feed, "*.nupkg"))
        {
            using var archive = ZipFile.OpenRead(package);
            using var stream = archive.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
            var groups = XDocument.Load(stream).Descendants().Where(e => e.Name.LocalName == "group");
            foreach (var group in groups.Where(g => (string?)g.Attribute("targetFramework") == "net10.0"))
            {
                foreach (var dependency in group.Elements().Where(e => e.Name.LocalName == "dependency"))
                {
                    var id = (string)dependency.Attribute("id")!;
                    if (!id.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
                        && !id.StartsWith("System.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var range = (string)dependency.Attribute("version")!;
                    var major = int.Parse(LeadingMajor().Match(range).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    Assert.True(
                        major <= 10,
                        $"{Path.GetFileName(package)}: the net10.0 group depends on {id} {range}. "
                        + "A .NET 10 consumer pinning transitive packages at 10.x fails restore (NU1109).");
                    checkedDependencies.Add($"{Path.GetFileName(package)}: {id} {range}");
                }
            }
        }

        Assert.NotEmpty(checkedDependencies);
        return checkedDependencies;
    }

    private static List<(string Name, Version Version)> RoslynReferences(ZipArchiveEntry entry)
    {
        using var buffer = new MemoryStream();
        using (var stream = entry.Open())
        {
            stream.CopyTo(buffer);
        }

        buffer.Position = 0;
        using var pe = new PEReader(buffer);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle => metadata.GetAssemblyReference(handle))
            .Select(reference => (Name: metadata.GetString(reference.Name), reference.Version))
            .Where(reference => reference.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            .ToList();
    }

    [GeneratedRegex(@"^analyzers/dotnet/roslyn(\d+)\.(\d+)/.*\.dll$", RegexOptions.None, 1000)]
    private static partial Regex RoslynFolder();

    [GeneratedRegex(@"(\d+)\.", RegexOptions.None, 1000)]
    private static partial Regex LeadingMajor();
}
