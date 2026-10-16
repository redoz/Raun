using System.Collections;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Raun.Model;
using Raun.Reporting;
using Raun.Reporting.Html;
using Raun.Running;
using Xunit;

namespace Raun.Generator.Test;

public class ScenarioCaseExecutionTests
{
    private const string Source = """
        using System;
        using System.Collections.Concurrent;
        using System.Threading.Tasks;
        using Raun;
        namespace CaseExecution;
        public static class Journal { public static ConcurrentQueue<string> Events { get; } = new(); }
        public sealed class Token : IDisposable
        {
            public Guid Id { get; } = Guid.NewGuid();
            public void Dispose() => Journal.Events.Enqueue("scope disposed " + Id);
        }
        public sealed class World : IScenarioWorld<World>, IDisposable
        {
            public Guid Id { get; } = Guid.NewGuid();
            public required Token Token { get; init; }
            public static ValueTask<World> CreateAsync(ScenarioContext context)
            {
                var world = new World { Token = (Token)context.Services!.GetService(typeof(Token))! };
                Journal.Events.Enqueue("created " + world.Id + " " + world.Token.Id);
                return ValueTask.FromResult(world);
            }
            public void Dispose() => Journal.Events.Enqueue("world disposed " + Id);
        }
        public sealed class Checks : Given<World>
        {
            [StepName("prepare {value}")]
            public Task<string> Prepare(string value)
            {
                Context.Log(World.Id + " " + World.Token.Id);
                return Task.FromResult(value);
            }
            [StepName("verify {value}")]
            public Task Verify(string value, bool succeed)
            {
                Context.Log(World.Id + " " + World.Token.Id);
                if (!succeed) throw new InvalidOperationException(value);
                return Task.CompletedTask;
            }
            [StepName("afterwards")]
            public Task Afterwards() => Task.CompletedTask;
        }
        public abstract class Suite : Scenarios<World> { public Checks Given => Steps<Checks>(); }
        public sealed class Cases : Suite
        {
            [Scenario("isolated case")]
            [ScenarioCase("pass", "one", true)]
            [ScenarioCase("fail", "two", false)]
            public async Task Run(string value, bool succeed)
            {
                var prepared = await Given.Prepare(value);
                await Given.Verify(prepared, succeed);
                await Given.Afterwards();
            }
        }
        """;

    [Fact]
    public async Task Cases_have_independent_worlds_scopes_outcomes_and_report_entries()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var definitions = generated.Definitions();
        var assembly = definitions[0].Method!.DeclaringType!.Assembly;
        await using var services = new ServiceCollection().AddScoped(assembly.GetType("CaseExecution.Token")!).BuildServiceProvider();
        var capture = new Capture();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".html");
        try
        {
            var bus = new RunEventBus([capture, new HtmlReportSink(path, TimeProvider.System)]);
            await new RunLoop(() => definitions, services: services).RunAsync(null, bus, CancellationToken.None);
            Assert.Empty(bus.Failures);
            Assert.Equal(2, capture.Finished.Count);
            var worlds = new List<string>();
            foreach (var finished in capture.Finished)
            {
                Assert.Single(finished.Results, r => r.Node.IsSetup && r.Status == StepStatus.Passed);
                Assert.Single(finished.Results, r => r.Node.IsTeardown && r.Status == StepStatus.Passed);
                var prepare = finished.Results.Single(r => r.Node.OperationName == "Prepare");
                var verify = finished.Results.Single(r => r.Node.OperationName == "Verify");
                Assert.Equal(Assert.Single(prepare.Logs), Assert.Single(verify.Logs));
                worlds.Add(prepare.Logs[0]);
                Assert.Equal(finished.Definition.DisplayName.EndsWith("[fail]", StringComparison.Ordinal) ? StepStatus.Failed : StepStatus.Passed, verify.Status);
            }
            Assert.Equal(2, worlds.Select(w => w.Split(' ')[0]).Distinct().Count());
            Assert.Equal(2, worlds.Select(w => w.Split(' ')[1]).Distinct().Count());
            var events = Events(assembly);
            foreach (var identity in worlds)
            {
                Assert.Contains("world disposed " + identity.Split(' ')[0], events);
                Assert.Contains("scope disposed " + identity.Split(' ')[1], events);
            }
            var html = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            const string marker = "<script id=\"model\" type=\"application/json\">";
            var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            using var model = JsonDocument.Parse(html[start..html.IndexOf("</script>", start, StringComparison.Ordinal)]);
            var scenarios = model.RootElement.GetProperty("scenarios").EnumerateArray().ToList();
            Assert.Equal(2, scenarios.Count);
            Assert.Equal(definitions.Select(d => d.DisplayName), scenarios.Select(s => s.GetProperty("displayName").GetString()));
            Assert.Equal(2, scenarios.Select(s => s.GetProperty("scenarioId").GetString()).Distinct().Count());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task One_case_step_uid_runs_only_its_predecessors_and_teardown()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var definitions = generated.Definitions();
        var selected = definitions[0];
        var assembly = selected.Method!.DeclaringType!.Assembly;
        await using var services = new ServiceCollection().AddScoped(assembly.GetType("CaseExecution.Token")!).BuildServiceProvider();
        var target = selected.Nodes.Single(n => n.OperationName == "Verify");
        var capture = new Capture();
        await new RunLoop(() => definitions, services: services).RunAsync(
            new UidNodeSelector(new HashSet<string> { StepUid.Of(selected, target) }), capture, CancellationToken.None);
        var finished = Assert.Single(capture.Finished);
        Assert.Equal(selected.ScenarioId, finished.Definition.ScenarioId);
        Assert.Equal(["Setup", "Prepare", "Verify", "Teardown"], capture.Steps.OrderBy(r => r.Node.Index).Select(r => r.Node.OperationName));
        Assert.All(capture.Steps, r => Assert.Equal(StepStatus.Passed, r.Status));
        var excluded = Assert.Single(finished.Results, r => r.Node.OperationName == "Afterwards");
        Assert.Equal(StepStatus.Skipped, excluded.Status);
        Assert.Equal(Raun.Scheduling.ScenarioScheduler.NotSelectedSkipReason, excluded.SkipReason);
        Assert.Single(Events(assembly), e => e.StartsWith("created ", StringComparison.Ordinal));
        Assert.Single(Events(assembly), e => e.StartsWith("world disposed ", StringComparison.Ordinal));
        Assert.Single(Events(assembly), e => e.StartsWith("scope disposed ", StringComparison.Ordinal));
    }

    private static string[] Events(System.Reflection.Assembly assembly)
        => ((IEnumerable)assembly.GetType("CaseExecution.Journal")!.GetProperty("Events")!.GetValue(null)!).Cast<string>().ToArray();

    private sealed class Capture : RunEventSink
    {
        public List<ScenarioFinished> Finished { get; } = [];
        public List<StepResult> Steps { get; } = [];
        protected override ValueTask OnStepFinishedAsync(StepFinished e) { Steps.Add(e.Result); return default; }
        protected override ValueTask OnScenarioFinishedAsync(ScenarioFinished e) { Finished.Add(e); return default; }
    }
}
