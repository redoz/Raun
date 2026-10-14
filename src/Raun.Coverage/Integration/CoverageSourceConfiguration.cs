using System.Diagnostics.CodeAnalysis;
using Microsoft.Testing.Platform.Services;
using Raun.Coverage;
using Raun.Coverage.Projects;

namespace Raun.Mtp.Coverage;

internal sealed class CoverageSourceConfiguration
{
    public CollectorScope Scope { get; private set; } = new();
    public string? Error { get; private set; }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Source-analysis setup failures must become coverage diagnostics, not prevent tests from running.")]
    public void Initialize(IServiceProvider services, string directory)
    {
        try
        {
            var configuration = services.GetConfiguration();
            Scope = CollectorSettingsReader.FromConfiguration(key => configuration[key]);
            var options = services.GetCommandLineOptions();
            if (options.IsOptionSet("coverlet"))
            {
                Scope = CoverletSettingsReader.Load(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? Path.Combine(AppContext.BaseDirectory, "test.dll"),
                    directory, option => options.TryGetOptionArgumentList(option, out var values) ? values : null, options.IsOptionSet);
            }
            else if (options.TryGetOptionArgumentList("coverage-settings", out var arguments) && arguments.Length > 0)
            {
                using var stream = File.OpenRead(Path.GetFullPath(arguments[0], directory));
                Scope = CollectorSettingsReader.Read(stream);
            }
            ClassifiedCoverageAnalyzer.Prepare(directory);
        }
        catch (Exception error)
        {
            Error = error.Message;
            NodeDiagnostics.Log("coverage-configuration-failure", error.ToString());
        }
    }
}
