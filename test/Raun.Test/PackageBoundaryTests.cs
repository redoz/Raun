using System.Xml.Linq;
using Xunit;

namespace Raun.Test;

/// <summary>
/// The packages meet at their public surface. One package reading another's internals is a contract
/// nobody can see, so only a test project may be given them.
/// </summary>
public class PackageBoundaryTests
{
    [Fact]
    public void Only_test_projects_are_shown_a_package_s_internals()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Raun.slnx"))) root = root.Parent;
        Assert.NotNull(root);

        var friends = Directory
            .EnumerateFiles(Path.Combine(root.FullName, "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(project => XDocument.Load(project).Descendants("InternalsVisibleTo")
                .Select(element => (Project: Path.GetFileNameWithoutExtension(project), Friend: (string?)element.Attribute("Include") ?? "")))
            .ToList();

        Assert.NotEmpty(friends);
        Assert.All(friends, friend => Assert.True(
            friend.Friend == friend.Project + ".Test",
            $"{friend.Project} shows its internals to {friend.Friend}; make what it needs public instead."));
    }
}
