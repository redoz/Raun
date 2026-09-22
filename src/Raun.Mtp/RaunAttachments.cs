using Microsoft.Testing.Platform.Configurations;
using Microsoft.Testing.Platform.Services;
using Microsoft.Testing.Platform.TestHost;

namespace Raun.Mtp;

/// <summary>
/// Where a step's attachments are written before they are published as MTP file artifacts.
/// </summary>
/// <remarks>
/// They used to go to <c>%TEMP%\raun-mtp\&lt;step uid&gt;</c> and stay there for ever: invisible to
/// whoever ran the tests, never cleaned up by anything, and carrying whatever a step chose to attach.
/// A run's output belongs in the run's own results directory (the one <c>--results-directory</c>
/// names and <c>--report-html</c> already writes to), where it is visible, scoped to one session, and
/// the user's to keep or delete. They are <em>not</em> deleted when the session closes: the artifacts
/// a run just published are the point of attaching them, and a TRX or an IDE may hold links to them.
/// Only an attachment folder that stayed empty is removed, so a results directory does not fill up
/// with one empty folder per run.
/// </remarks>
internal static class RaunAttachments
{
    // MTP's well-known results-directory configuration key (PlatformConfigurationConstants is internal).
    private const string ResultsDirectoryKey = "platformOptions:resultDirectory";

    /// <summary>The folder name created under the results directory.</summary>
    public const string FolderName = "raun-attachments";

    /// <summary>Pure resolution: the attachment root for one session under a results directory.</summary>
    public static string Resolve(string? resultsDirectory, string sessionUid)
    {
        var dir = string.IsNullOrEmpty(resultsDirectory) ? Directory.GetCurrentDirectory() : resultsDirectory;
        return Path.Combine(dir, FolderName, SanitizeFileName(sessionUid));
    }

    /// <summary>Reads the results directory off the framework's MTP service provider.</summary>
    public static string Resolve(IServiceProvider? services, SessionUid sessionUid)
    {
        string? resultsDirectory = null;
        if (services is not null)
        {
            try
            {
                IConfiguration configuration = services.GetConfiguration();
                resultsDirectory = configuration[ResultsDirectoryKey];
            }
            catch (InvalidOperationException)
            {
                // A provider that is not a platform one (the framework can be driven directly in
                // tests, and its parameterless ctor has no services at all): fall back below.
            }
        }

        return Resolve(resultsDirectory, sessionUid.Value);
    }

    /// <summary>Removes the session's attachment folder when nothing was written into it. Best-effort:
    /// a folder that cannot be removed is left alone rather than failing the session.</summary>
    public static void CleanUp(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            if (Directory.EnumerateFileSystemEntries(root).Any())
            {
                return;
            }

            Directory.Delete(root);

            // The shared parent is worth removing too when this was the only session in it.
            var parent = Path.GetDirectoryName(root);
            if (parent is not null
                && Path.GetFileName(parent) == FolderName
                && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Replaces every character that cannot appear in a file name with an underscore.</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        return builder.ToString();
    }
}
