using System.ComponentModel;
using Raun;

namespace Raun.PackageTests;

[DisplayName("Package feed")]
public sealed record PackageFeed(string Directory, string Version) : IResource<PackageFeed>
{
    public static ResourceKey KeyFor(PackageFeed feed) => feed.Directory;
    public static string LabelFor(PackageFeed feed) => $"Raun {feed.Version}";
}

[DisplayName("Report")]
public sealed record ConsumerReport(string File, string Consumer) : IResource<ConsumerReport>
{
    public static ResourceKey KeyFor(ConsumerReport report) => report.File;
    public static string LabelFor(ConsumerReport report) => $"{report.Consumer} report";
}

public sealed class RestoredAttribute : EditedAttribute;
public sealed class BuiltAttribute : EditedAttribute;
