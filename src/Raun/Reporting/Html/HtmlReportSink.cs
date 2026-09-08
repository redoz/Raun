using System.Text.Json;
using Raun.Reporting;

namespace Raun.Reporting.Html;

/// <summary>
/// Subscribes to the run-event stream, accumulates the <see cref="HtmlReportModel"/>, and on
/// <see cref="RunFinished"/> renders the embedded template with the model's JSON and writes one
/// self-contained HTML file. Best-effort I/O: a write failure propagates out of
/// <see cref="OnRunFinishedAsync"/> so the <see cref="RunEventBus"/> records it in
/// <see cref="RunEventBus.Failures"/> and the framework logs it — a broken report must never
/// fail the run (design §3.D/§3.E). Constructed only when <c>--report-html</c> is set.
/// </summary>
public sealed class HtmlReportSink : RunEventSink
{
    private const string JsonToken = "/*__RAUN_REPORT_JSON__*/";
    private const string ResourceName = "Raun.Reporting.Html.report-template.html";

    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly HtmlReportModelBuilder _builder = new();

    public HtmlReportSink(string path, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _path = path;
        _timeProvider = timeProvider;
    }

    protected override ValueTask OnRunStartedAsync(RunStarted e)
    {
        _builder.OnRunStarted(e.Scenarios);
        return default;
    }

    protected override ValueTask OnScenarioStartedAsync(ScenarioStarted e)
    {
        _builder.OnScenarioStarted(e.Definition, e.Waited, e.WaitedFor);
        return default;
    }

    protected override ValueTask OnStepFinishedAsync(StepFinished e)
    {
        _builder.OnStepFinished(e.Definition, e.Result);
        return default;
    }

    protected override async ValueTask OnRunFinishedAsync(RunFinished e)
    {
        var generatedAt = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        var model = _builder.Build(generatedAt);
        var json = JsonSerializer.Serialize(model, SerializerOptions);
        var html = LoadTemplate().Replace(JsonToken, json, StringComparison.Ordinal);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(_path, html).ConfigureAwait(false);
    }

    private static string LoadTemplate()
    {
        using var stream = typeof(HtmlReportSink).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded report template '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
