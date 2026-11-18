using Microsoft.Testing.Platform.Extensions.Messages;
using Microsoft.Testing.Platform.Messages;
using Microsoft.Testing.Platform.Requests;
using Microsoft.Testing.Platform.TestHost;
using Raun.Model;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>
/// What a run with <c>--report-html</c> tells the platform about the report: where it is when it was
/// written, and, on the console, that there is none when it could not be written.
/// </summary>
public sealed class HtmlReportAnnouncementTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "raun-report-announce", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static void RegisterOneScenario()
        => ScenarioRegistry.Register($"Raun.Mtp.Test.ReportAnnounce.{Guid.NewGuid():N}", () => new ScenarioDefinition
        {
            ScenarioId = "announce-scn",
            DisplayName = "announce scenario",
            MethodName = "Ns.Announce",
            Nodes =
            [
                new ScenarioNode
                {
                    Index = 0,
                    StepId = "a",
                    Phase = "Given",
                    OperationName = "Op",
                    DisplayNameTemplate = "a",
                    DependsOn = [],
                    Invoke = (_, _) => Task.FromResult<object?>(null),
                },
            ],
        });

    private static async Task<RecordingMessageBus> RunAsync(TestPlatformServices services)
    {
        RegisterOneScenario();
        var framework = new RaunTestFramework(services);
        var uid = new SessionUid("announce-" + Guid.NewGuid().ToString("N"));
        await framework.CreateTestSession(uid);
        var bus = new RecordingMessageBus();

        // Only this test's scenario: the registry is shared with every other test of the assembly.
        var filter = new TestNodeUidListFilter([new TestNodeUid("announce-scn:a")]);
        await framework.OnExecute(uid, filter, bus, () => { }, CancellationToken.None);
        return bus;
    }

    [Fact]
    public async Task A_written_report_is_published_as_an_artifact_of_the_session()
    {
        Directory.CreateDirectory(_dir);
        var services = new TestPlatformServices()
            .WithOption("report-html")
            .WithOption("report-html-filename", "run.html")
            .WithConfiguration(RaunAttachments.ResultsDirectoryKey, _dir);

        var bus = await RunAsync(services);

        var artifact = Assert.Single(bus.Artifacts);
        Assert.Equal(Path.Combine(_dir, "run.html"), artifact.FileInfo.FullName);
        Assert.True(artifact.FileInfo.Exists);
        Assert.Empty(services.Printed);
    }

    [Fact]
    public async Task A_report_that_could_not_be_written_is_said_on_the_console_and_not_published()
    {
        // The results "directory" is a file, so nothing can be written below it.
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "x", TestContext.Current.CancellationToken);
        var services = new TestPlatformServices()
            .WithOption("report-html")
            .WithOption("report-html-filename", "run.html")
            .WithConfiguration(RaunAttachments.ResultsDirectoryKey, blocker);

        var bus = await RunAsync(services);

        Assert.Empty(bus.Artifacts);
        var printed = Assert.Single(services.Printed);
        Assert.Contains("HTML report could not be written", printed, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(blocker, "run.html"), printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_without_the_report_option_announces_nothing()
    {
        var services = new TestPlatformServices();

        var bus = await RunAsync(services);

        Assert.Empty(bus.Artifacts);
        Assert.Empty(services.Printed);
    }

    private sealed class RecordingMessageBus : IMessageBus
    {
        private readonly List<SessionFileArtifact> artifacts = [];

        public IReadOnlyList<SessionFileArtifact> Artifacts
        {
            get { lock (artifacts) { return [.. artifacts]; } }
        }

        public Task PublishAsync(IDataProducer dataProducer, IData data)
        {
            if (data is SessionFileArtifact artifact)
            {
                lock (artifacts) { artifacts.Add(artifact); }
            }

            return Task.CompletedTask;
        }
    }
}
