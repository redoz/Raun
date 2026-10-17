using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using Raun.Reporting.Coverage;
using Raun.Reporting;

namespace Raun.Reporting.Html;

/// <summary>
/// Subscribes to the run-event stream, accumulates the <see cref="HtmlReportModel"/>, and on
/// <see cref="RunFinished"/> renders the embedded template with the model's JSON and writes one
/// self-contained HTML file. Best-effort I/O: a write failure propagates out of
/// <see cref="OnRunFinishedAsync"/> so the <see cref="RunEventBus"/> records it in
/// <see cref="RunEventBus.Failures"/> and the host logs it — a broken report must never fail
/// the run. A host constructs one when the report was asked for (under Microsoft.Testing.Platform,
/// <c>--report-html</c>).
/// </summary>
public sealed class HtmlReportSink : RunEventSink
{
    private const string JsonToken = "/*__RAUN_REPORT_JSON__*/";
    private const string ResourceName = "Raun.Reporting.Html.report-template.html";

    private static readonly JsonSerializerOptions SerializerOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

    private readonly string _path;
    private readonly TimeProvider _timeProvider;
    private readonly HtmlReportModelBuilder _builder;

    private readonly object _gate = new();
    private HtmlReportModel? _model;
    private ReportCoverage? _coverage;
    private Task _lastWrite = Task.CompletedTask;

    public HtmlReportSink(string path, TimeProvider timeProvider) : this(path, timeProvider, false) { }

    public HtmlReportSink(string path, TimeProvider timeProvider, bool coverageRequested) : this(path, timeProvider, coverageRequested, null) { }

    /// <summary>Creates a report sink with optional repository source links.</summary>
    public HtmlReportSink(string path, TimeProvider timeProvider, bool coverageRequested, HtmlReportSourceOptions? sourceOptions)
    {
        sourceOptions?.Validate();
        _builder = new HtmlReportModelBuilder(sourceOptions);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _coverage = coverageRequested ? new ReportCoverage() : null;
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

    protected override ValueTask OnRunFinishedAsync(RunFinished e)
    {
        lock (_gate)
        {
            _model = _builder.Build(_timeProvider.GetUtcNow().UtcDateTime.ToString("O"));
            return new ValueTask(QueueWrite(CancellationToken.None));
        }
    }

    /// <summary>Enriches the retained run, or retains coverage until the run finishes.</summary>
    public Task SetCoverageAsync(ReportCoverage coverage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        lock (_gate)
        {
            _coverage = coverage;
            return _model is null ? Task.CompletedTask : QueueWrite(cancellationToken);
        }
    }

    /// <summary>Enriches a completed report when a collector publishes from the controller process.</summary>
    public static async Task EnrichFileAsync(string path, ReportCoverage coverage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(coverage);
        var html = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        const string marker = "<script id=\"model\" type=\"application/json\">";
        var markerStart = html.IndexOf(marker, StringComparison.Ordinal);
        if (markerStart < 0) { throw new InvalidDataException("The file does not contain a Raun report payload."); }
        var start = markerStart + marker.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (end < 0) { throw new InvalidDataException("The Raun report payload is incomplete."); }
        var payload = JsonNode.Parse(html.Substring(start, end - start)) as JsonObject
            ?? throw new InvalidDataException("The Raun report payload must be an object.");
        payload["coverage"] = JsonSerializer.SerializeToNode(coverage, SerializerOptions);
        var enriched = string.Concat(html.AsSpan(0, start), EscapeEmbeddedJson(payload.ToJsonString(SerializerOptions)), html.AsSpan(end));
        await HtmlReportWriter.WriteAsync(path, enriched, cancellationToken).ConfigureAwait(false);
    }

    private Task QueueWrite(CancellationToken cancellationToken)
    {
        var previous = _lastWrite;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastWrite = completion.Task;
        return WriteAsync(previous, completion, _model! with { Coverage = _coverage }, cancellationToken);
    }

    private async Task WriteAsync(Task previous, TaskCompletionSource completion, HtmlReportModel model, CancellationToken cancellationToken)
    {
        try
        {
            await previous.ConfigureAwait(false);
            var json = EscapeEmbeddedJson(JsonSerializer.Serialize(model, SerializerOptions));
            var html = LoadTemplate().Replace(JsonToken, json, StringComparison.Ordinal);
            await HtmlReportWriter.WriteAsync(_path, html, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The queue baton always completes, so a failed write cannot poison a later update.
            completion.TrySetResult();
        }
    }

    // Unicode stays readable; only HTML markup needs escaping at the script boundary.
    private static string EscapeEmbeddedJson(string json) => json.Replace("<", "\\u003C", StringComparison.Ordinal);

    private static string LoadTemplate()
    {
        using var stream = typeof(HtmlReportSink).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded report template '{ResourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
