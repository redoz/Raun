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

public sealed class RestoredAttribute : UpdatedAttribute;
public sealed class BuiltAttribute : UpdatedAttribute;

/// <summary>Create an isolated consumer and link it to its package feed.</summary>
public sealed class Isolated : CreatedAttribute
{
    /// <summary>Read the package feed used to configure the consumer.</summary>
    public sealed class FeedAttribute : ResourceLinkAttribute<PackageFeed, ReadAttribute>;
}

/// <summary>Create a report and link it to the consumer that produced it.</summary>
public sealed class Reported : CreatedAttribute
{
    /// <summary>Read the consumer that produced the report.</summary>
    public sealed class ConsumerAttribute : ResourceLinkAttribute<PackageConsumer, ReadAttribute>;
}
