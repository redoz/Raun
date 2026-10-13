using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Raun.Coverage.Projects;

internal static partial class SolutionLocator
{
    public static string? Resolve(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var solutions = current.EnumerateFiles().Where(f => f.Extension is ".sln" or ".slnx")
                .OrderByDescending(file => file.Extension == ".slnx").ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();
            if (solutions.Length == 0) { continue; }
            if (solutions.Length > 1)
            {
                var projects = Projects(solutions[0]);
                if (!solutions.Skip(1).All(solution => projects.SetEquals(Projects(solution))))
                {
                    throw new InvalidOperationException("Several solutions are applicable; classified coverage cannot select one unambiguously.");
                }
            }
            return solutions[0].FullName;
        }
        return null;
    }

    internal static HashSet<string> Projects(FileInfo solution)
    {
        IEnumerable<string> paths;
        if (solution.Extension == ".slnx")
        {
            using var reader = XmlReader.Create(solution.FullName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            paths = XDocument.Load(reader).Descendants("Project").Select(node => (string?)node.Attribute("Path") ?? "").ToArray();
        }
        else
        {
            paths = ProjectEntry().Matches(File.ReadAllText(solution.FullName)).Select(match => match.Groups[1].Value)
                .Where(path => path.EndsWith("proj", StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        return paths.Select(path => Path.GetFullPath(Path.Combine(solution.DirectoryName!, path.Replace('\\', Path.DirectorySeparatorChar))))
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    [GeneratedRegex("^Project\\([^)]*\\) = \"[^\"]*\", \"([^\"]+)\"", RegexOptions.Multiline)]
    private static partial Regex ProjectEntry();
}
