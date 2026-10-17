using System.Globalization;

namespace Raun.Reporting.Html;

/// <summary>Sets the repository location for source links in an HTML report.</summary>
public sealed class HtmlReportSourceOptions
{
    /// <summary>The repository file-view URL, including the branch or commit. For GitHub, use
    /// <c>https://github.com/owner/repo/blob/revision/</c>. For Azure DevOps, use
    /// <c>https://dev.azure.com/org/project/_git/repo?version=GCrevision</c>.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>The local repository root used to make source paths relative. When omitted,
    /// Raun searches the source file's parent directories for a <c>.git</c> file or directory.
    /// Set this path to the build checkout root when source files are not available at report time.</summary>
    public string? SourceRoot { get; init; }

    internal void Validate()
    {
        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || BaseUrl.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The report source base URL must be an absolute HTTP or HTTPS URL.", nameof(BaseUrl));
        }
        if (SourceRoot is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(SourceRoot);
        }
    }

    internal string? GetUrl(string? sourceFile, int line)
    {
        if (string.IsNullOrEmpty(sourceFile) || line <= 0) { return null; }
        var root = SourceRoot ?? FindRepositoryRoot(sourceFile);
        if (root is null) { return null; }
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(sourceFile)).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) { return null; }
        var address = new UriBuilder(BaseUrl);
        if (BaseUrl.AbsolutePath.Contains("/_git/", StringComparison.OrdinalIgnoreCase))
        {
            var parameters = address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(value => !new[] { "path", "line", "lineEnd", "lineStartColumn", "lineEndColumn", "lineStyle" }
                    .Contains(value.Split('=')[0], StringComparer.OrdinalIgnoreCase));
            address.Query = string.Join("&", parameters.Append("path=" + Uri.EscapeDataString("/" + relative)))
                + "&line=" + line.ToString(CultureInfo.InvariantCulture)
                + "&lineEnd=" + ((long)line + 1).ToString(CultureInfo.InvariantCulture)
                + "&lineStartColumn=1&lineEndColumn=1&lineStyle=plain";
            address.Fragment = "";
        }
        else
        {
            address.Path = address.Path.TrimEnd('/') + "/" + string.Join("/", relative.Split('/').Select(Uri.EscapeDataString));
            address.Fragment = "L" + line.ToString(CultureInfo.InvariantCulture);
        }
        return address.Uri.AbsoluteUri;
    }

    private static string? FindRepositoryRoot(string sourceFile)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(sourceFile));
        while (directory is not null)
        {
            var marker = Path.Combine(directory, ".git");
            if (File.Exists(marker) || Directory.Exists(marker)) { return directory; }
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }
}
