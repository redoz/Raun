using System.Globalization;
using Raun;
using Raun.Model;

namespace Raun.Reporting.Html;

/// <summary>
/// Builds the deterministic <see cref="HtmlReportModel"/> from the run-event stream. All layout
/// (lane packing, resource rollup, ms-offset reduction) happens here — not in the renderer — so the
/// JSON is snapshot-testable (design §4). Drive it with <see cref="OnRunStarted"/> (the canonical
/// scenario order), <see cref="OnScenarioStarted"/>, one <see cref="OnStepFinished"/> per terminal
/// step, then <see cref="Build"/>. Scenarios run concurrently, so step events of different
/// scenarios interleave; each is routed by its definition, and the report keeps the run's order.
/// </summary>
internal sealed class HtmlReportModelBuilder(HtmlReportSourceOptions? sourceOptions = null)
{
    private readonly List<ScenarioAccumulator> _scenarios = [];

    /// <summary>Lays the scenarios out in the run's canonical order before any of them starts.</summary>
    public void OnRunStarted(IReadOnlyList<ScenarioDefinition>? scenarios)
    {
        if (scenarios is null)
        {
            return;
        }

        foreach (var definition in scenarios)
        {
            if (Find(definition.ScenarioId) is null)
            {
                _scenarios.Add(new ScenarioAccumulator(definition));
            }
        }
    }

    public void OnScenarioStarted(ScenarioDefinition definition, TimeSpan waited = default, Type? waitedFor = null)
    {
        var acc = Find(definition.ScenarioId);
        if (acc is null)
        {
            // Driven without a RunStarted order (a sink under unit test): append as they come.
            acc = new ScenarioAccumulator(definition);
            _scenarios.Add(acc);
        }

        acc.Started = true;
        acc.Waited = waited;
        acc.WaitedFor = waitedFor;
    }

    public void OnStepFinished(ScenarioDefinition definition, StepResult result)
    {
        var acc = Find(definition.ScenarioId)
                  ?? throw new InvalidOperationException(
                      $"StepFinished for '{definition.ScenarioId}' before its ScenarioStarted.");
        acc.Add(result);
    }

    public HtmlReportModel Build(string generatedAtUtc)
    {
        // A scenario the run never launched (cancellation mid-run, a faulted sibling, a mis-declared
        // token halting further admission) has nothing to report — it never had a Status, and
        // without this filter Build() below would default it to "passed" with zero steps.
        var scenarios = _scenarios.Where(s => s.Started).Select((s, index) => s.Build(index, sourceOptions)).ToList();

        // Wall clock: earliest start to latest end over the scenarios that ran, not the sum — with
        // concurrent scenarios the sum would count overlapping time twice.
        var ran = _scenarios.Where(s => s.HasSteps).ToList();
        var totalMs = 0d;
        if (ran.Count > 0)
        {
            var origin = ran.Min(s => s.Start);
            totalMs = ran.Max(s => (s.Start - origin).TotalMilliseconds + s.DurationMs);
        }

        var summary = new ReportSummary
        {
            Passed = scenarios.Count(s => s.Status == "passed"),
            Failed = scenarios.Count(s => s.Status == "failed"),
            Skipped = scenarios.Count(s => s.Status == "skipped"),
            TotalMs = totalMs,
        };

        return new HtmlReportModel
        {
            GeneratedAtUtc = generatedAtUtc,
            Summary = summary,
            Scenarios = scenarios,
        };
    }

    private ScenarioAccumulator? Find(string scenarioId)
        => _scenarios.Find(s => s.Definition.ScenarioId == scenarioId);

    private sealed class ScenarioAccumulator(ScenarioDefinition definition)
    {
        private readonly List<StepResult> _results = [];
        public ScenarioDefinition Definition { get; } = definition;

        /// <summary>Set by <see cref="HtmlReportModelBuilder.OnScenarioStarted"/>. False means the run never launched this
        /// scenario (cancellation mid-run, a faulted sibling, a mis-declared token halting further
        /// admission) — it has no place in the report, just as it has no MTP node.</summary>
        public bool Started { get; set; }

        public TimeSpan Waited { get; set; }

        public Type? WaitedFor { get; set; }

        // SkipScenarioAsync reports a scenario that never ran with StartedAt = default; such results
        // have no place on a timeline.
        private IEnumerable<StepResult> Timed => _results.Where(r => r.StartedAt != default);

        public bool HasSteps => Timed.Any();

        public DateTimeOffset Start => Timed.Any() ? Timed.Min(r => r.StartedAt) : DateTimeOffset.UnixEpoch;

        /// <summary>Latest step end relative to <see cref="Start"/>, in ms; 0 with no timed steps.</summary>
        public double DurationMs => Timed.Any()
            ? Timed.Max(r => Ms(r.StartedAt - Start) + Ms(r.Duration))
            : 0;

        public void Add(StepResult result) => _results.Add(result);

        public ReportScenario Build(int scenarioIndex, HtmlReportSourceOptions? sourceOptions)
        {
            var start = Start;

            var ordered = _results.OrderBy(r => r.Node.Index).ToList();
            var lanes = PackLanes(ordered, start);
            // Ordinary steps share the MTP labels; the HTML report gives unnumbered Setup an explicit zero.
            var labels = StepNumbering.Compute(Definition);

            // Actual CLR identities are the sole lookup key. Labels never participate in identity.
            var identities = new Dictionary<ResourceIdentity, string>();
            string ResourceId(ResourceIdentity identity)
            {
                if (!identities.TryGetValue(identity, out var id))
                {
                    id = "r" + scenarioIndex.ToString(CultureInfo.InvariantCulture) + "-"
                        + identities.Count.ToString(CultureInfo.InvariantCulture);
                    identities.Add(identity, id);
                }

                return id;
            }

            ReportMessage Message(StepMessage message, string prefix = "")
            {
                var parts = new List<ReportMessagePart>();
                if (prefix.Length > 0) { parts.Add(new ReportMessagePart { Text = prefix }); }
                parts.AddRange(message.Parts.Select(part => new ReportMessagePart
                {
                    Text = part.Text,
                    ResourceId = part.Resource is { } identity ? ResourceId(identity) : null,
                    Data = part.Snapshot ?? part.Data,
                    DataTruncated = part.Snapshot is null && part.DataTruncated,
                }));
                return new ReportMessage { Text = prefix + message.Text, Parts = parts };
            }

            var steps = new List<ReportStep>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                // A result with StartedAt = default never ran (SkipScenarioAsync); it has no real
                // offset from start, so pin it to 0 rather than subtracting into a huge negative span.
                var offset = r.StartedAt == default ? 0 : Ms(r.StartedAt - start);
                steps.Add(new ReportStep
                {
                    StepId = r.Node.StepId,
                    Index = r.Node.Index,
                    Label = r.Node.IsSetup ? "0" : labels[r.Node.Index],
                    IsLifecycle = r.Node.IsSetup || r.Node.IsTeardown,
                    Phase = r.Node.Phase,
                    DisplayName = r.DisplayName,
                    TitleMessage = Message(r.TitleMessage),
                    NoteMessages = r.NoteMessages.Select(note => Message(note)).ToList(),
                    LogMessages = r.LogEntries.Count > 0
                        ? r.LogEntries.Select(e => Message(e.Content ?? StepMessage.FromText(string.Empty),
                            "+" + e.Elapsed.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "s ")).ToList()
                        : r.Logs.Select(line => Message(StepMessage.FromText(line))).ToList(),
                    Status = StatusText(r.Status),
                    OffsetMs = offset,
                    DurationMs = Ms(r.Duration),
                    Lane = lanes[i],
                    // The report graph includes value, ordering, guard, and merge edges.
                    DependsOn = r.Node.DependsOn.Concat(r.Node.WaitsFor)
                        .Concat(r.Node.Guards.Select(guard => guard.ConditionIndex))
                        .Concat(r.Node.MergeSources).Distinct().Order().ToArray(),
                    Dependencies = r.Node.WaitsFor.Count == 0 && r.Node.Guards.Count == 0 && r.Node.MergeSources.Count == 0 ? null
                        : r.Node.DependsOn.Select(index => new ReportDependency(index, "dependency"))
                            .Concat(r.Node.WaitsFor.Select(index => new ReportDependency(index, "ordering")))
                            .Concat(r.Node.Guards.Select(guard => new ReportDependency(guard.ConditionIndex, "condition")))
                            .Concat(r.Node.MergeSources.Select(index => new ReportDependency(index, "alternative")))
                            .Distinct().ToArray(),
                    GroupId = r.Node.GroupId,
                    Notes = r.Notes,
                    // Timer-prefixed ("+1.234s …") when the scheduler supplied offsets; plain lines otherwise.
                    Logs = r.LogEntries.Count > 0 ? r.LogEntries.Select(e => e.ToString()).ToList() : r.Logs,
                    TraceId = r.TraceId,
                    SpanId = r.SpanId,
                    Attachments = r.Attachments
                        .OrderBy(a => a.Key, StringComparer.Ordinal)
                        .Select(a => new ReportAttachment { Name = a.Key, Value = a.Value })
                        .ToList(),
                    Effects = r.Effects.Select(e => new ReportEffect
                    {
                        ResourceId = ResourceId(e.Identity),
                        Verb = e.Verb.ToString(),
                        ActionName = e.Action?.Name,
                        Label = e.Label,
                        Type = e.Identity.Kind.DisplayName,
                        Key = e.Identity.Key.ToString(),
                        OffsetMs = Ms(e.Timestamp - start),
                        Data = e.DataSnapshotCaptured ? e.DataSnapshot : e.Data?.ToString(),
                    }).ToList(),
                    Exception = r.Exception?.ToString(),
                    SkipReason = r.SkipReason,
                });
            }

            // Lineage relations (2026-06-22 spec): relations are recorded explicitly at runtime from each
            // producer's normalized relationship declarations. Map them straight
            // through; dedup by (subject, target, kind) across the scenario. No subject inference.
            var references = new List<ReportReference>();
            var seenRelations = new HashSet<(ResourceIdentity, ResourceIdentity, LifecycleVerb)>();
            foreach (var r in ordered)
            {
                foreach (var relation in r.Lineage)
                {
                    var subjectType = relation.Subject.Kind.DisplayName;
                    var subjectKey = relation.Subject.Key.ToString();
                    var targetType = relation.Target.Kind.DisplayName;
                    var targetKey = relation.Target.Key.ToString();
                    if (!seenRelations.Add((relation.Subject, relation.Target, relation.Kind)))
                    {
                        continue;
                    }

                    references.Add(new ReportReference
                    {
                        SubjectId = ResourceId(relation.Subject),
                        TargetId = ResourceId(relation.Target),
                        SubjectType = subjectType,
                        SubjectKey = subjectKey,
                        TargetType = targetType,
                        TargetKey = targetKey,
                        Kind = relation.Kind.ToString(),
                    });
                }
            }

            // Mentions and lineage endpoints get entries, but only actual effects add activity.
            var events = ordered.SelectMany(r => r.Effects).ToLookup(e => e.Identity);
            // Choose a stable column name from frozen evidence in authored order. A fallback from
            // an earlier key-only observation must not hide a subsequently observed readable name.
            var resourceLabels = ordered.SelectMany(result => result.Effects.Select(effect => (effect.Identity, effect.Label))
                .Concat(result.TitleMessage.Parts.Concat(result.NoteMessages.SelectMany(note => note.Parts))
                    .Concat(result.LogEntries.SelectMany(entry => entry.Content?.Parts ?? []))
                    .Where(part => part.Resource.HasValue).Select(part => (Identity: part.Resource!.Value, part.Label))))
                .Where(observation => !string.IsNullOrWhiteSpace(observation.Label)
                    && !string.Equals(observation.Label, ResourcePresentation.Display(observation.Identity), StringComparison.Ordinal))
                .ToLookup(observation => observation.Identity, observation => observation.Label);

            var resources = identities.Select(pair => new ReportResource
            {
                Id = pair.Value,
                Label = resourceLabels[pair.Key].FirstOrDefault(),
                QualifiedType = pair.Key.Kind.QualifiedName,
                Type = pair.Key.Kind.DisplayName,
                Key = pair.Key.Key.ToString(),
                Events = events[pair.Key].Select(e => new ReportResourceEvent
                {
                    Verb = e.Verb.ToString(),
                        ActionName = e.Action?.Name,
                    OffsetMs = Ms(e.Timestamp - start),
                    StepId = e.StepId ?? string.Empty,
                }).ToList(),
            }).ToList();

            var status = steps.Any(s => s.Status == "failed") ? "failed"
                : steps.Any(s => s.Status == "skipped") ? "skipped"
                : "passed";

            var durationMs = steps.Count == 0 ? 0 : steps.Max(s => s.OffsetMs + s.DurationMs);

            return new ReportScenario
            {
                ScenarioId = Definition.ScenarioId,
                DisplayName = Definition.DisplayName,
                ClassDisplayName = Definition.ClassDisplayName,
                MethodName = Definition.MethodName,
                SourceUrl = sourceOptions?.GetUrl(Definition.SourceFile, Definition.SourceLine),
                TypeSourceUrl = sourceOptions?.GetUrl(Definition.TypeSourceFile, Definition.TypeSourceLine),
                StartedAtUtc = start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                DurationMs = durationMs,
                Status = status,
                Steps = steps,
                Resources = resources,
                References = references,
                Uses = Definition.Uses.Select(u => $"{u.Resource.Name}:{u.Mode}").ToList(),
                WaitedMs = Ms(Waited),
                WaitedFor = WaitedFor?.Name,
            };
        }

        // Greedy interval packing: a step takes the first lane whose last bar ended at/before its start.
        private static int[] PackLanes(List<StepResult> ordered, DateTimeOffset start)
        {
            var laneEnds = new List<double>();
            var lanes = new int[ordered.Count];
            for (var i = 0; i < ordered.Count; i++)
            {
                var s = ordered[i].StartedAt == default ? 0 : Ms(ordered[i].StartedAt - start);
                var e = s + Ms(ordered[i].Duration);
                var lane = -1;
                for (var l = 0; l < laneEnds.Count; l++)
                {
                    if (laneEnds[l] <= s) { lane = l; break; }
                }

                if (lane < 0) { lane = laneEnds.Count; laneEnds.Add(e); }
                else { laneEnds[lane] = e; }

                lanes[i] = lane;
            }

            return lanes;
        }

        private static double Ms(TimeSpan span) => span.TotalMilliseconds;

        private static string StatusText(StepStatus status) => status switch
        {
            StepStatus.Passed => "passed",
            StepStatus.Failed => "failed",
            StepStatus.Skipped => "skipped",
            // A branch arm that was not chosen is a decision, not a skip: its own status, so a scenario
            // whose executed steps all passed stays passed however many arms it did not take.
            StepStatus.NotTaken => "not-taken",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }
}
