using Microsoft.Extensions.DependencyInjection;
using Raun.Model;
using Raun.Reporting;
using Raun.Running;
using Xunit;

namespace Raun.Generator.Test;

public sealed class ResourceActionCaseExecutionTests
{
    private const string Source = """
        using System.ComponentModel;
        using System.Threading.Tasks;
        using Raun;
        namespace ActionCases;
        [DisplayName("Approve")] public abstract class ApprovalAttribute : EditedAttribute;
        public sealed class ApprovedAttribute : ApprovalAttribute;
        public sealed class AuditedAttribute : ReadAttribute;
        public sealed record Patient(string Id, string Name) : IResource<Patient>
        {
            public static ResourceKey KeyFor(Patient value) => value.Id;
            public static string LabelFor(Patient value) => value.Name;
        }
        public sealed class Steps : Given<NoWorld>
        {
            [return: Loaded]
            public Task<Patient> Seed(string name) => Task.FromResult(new Patient("42", name));

            [Approved]
            [StepName("Approve {patient.Name}; {$return ?? patient}")]
            public Task<Patient> Approve([Read] Patient patient)
                => Task.FromResult(patient with { Name = patient.Name + " approved" });

            [StepName("Audit {id}")]
            public Task Audit([Audited, ResourceKey<Patient>] string id)
            {
                var key = new ResourceIdentity(typeof(Patient), id);
                Context.Log($"{key} | {key:id} | {key:data}");
                return Task.CompletedTask;
            }

            [StepName("Manual audit {id}")]
            public async Task ManualAudit([Read, ResourceKey<Patient>] string id)
            {
                await Context.Resources.Apply(ResourceAction.For<AuditedAttribute>(), new ResourceIdentity(typeof(Patient), id));
            }
        }
        public sealed class Cases : Scenarios<NoWorld>
        {
            public Steps Given => Steps<Steps>();
            [Scenario("approve one patient")]
            [ScenarioCase("first desk", "Ada")]
            [ScenarioCase("second desk", "Linnea")]
            public async Task ApprovePatient(string name)
            {
                var patient = await Given.Seed(name);
                var approved = await Given.Approve(patient);
                await Given.Audit(approved.Id);
                await Given.ManualAudit(approved.Id);
            }
        }
        """;

    [Fact]
    public async Task Cases_keep_actions_and_remembered_labels_separate_with_a_shared_resolver()
    {
        var generated = GeneratorHarness.Run(Source);
        generated.AssertCompiles();
        var definitions = generated.Definitions();
        Assert.Equal(2, definitions.Count);
        await using var services = new ServiceCollection().AddSingleton(new ResourceIdentityResolver()).BuildServiceProvider();
        var capture = new Capture();
        await new RunLoop(() => definitions, services: services).RunAsync(null, capture, TestContext.Current.CancellationToken);
        Assert.Equal(2, capture.Finished.Count);
        foreach (var finished in capture.Finished)
        {
            var name = finished.Definition.DisplayName.EndsWith("[first desk]", StringComparison.Ordinal) ? "Ada" : "Linnea";
            Assert.All(finished.Results, result => Assert.Equal(StepStatus.Passed, result.Status));
            var approve = Assert.Single(finished.Results, result => result.Node.OperationName == "Approve");
            Assert.Equal($"Approve {name}; {name} approved", approve.TitleMessage!.Text);
            var approval = Assert.Single(approve.Effects, effect => effect.Action is not null);
            Assert.Single(approve.Effects, effect => effect.Verb == LifecycleVerb.Read && effect.Action is null);
            Assert.Equal(LifecycleVerb.Edit, approval.Verb);
            Assert.Equal(LockMode.Exclusive, approval.Mode);
            Assert.Equal("Approve", approval.Action!.Name);
            Assert.Equal("ApprovedAttribute", approval.Action.AttributeType!.Name);
            Assert.Equal(name + " approved", approval.Label);
            var audit = Assert.Single(finished.Results, result => result.Node.OperationName == "Audit");
            var manual = Assert.Single(finished.Results, result => result.Node.OperationName == "ManualAudit");
            Assert.Equal("Audit " + name + " approved", audit.TitleMessage!.Text);
            Assert.Equal("Manual audit " + name + " approved", manual.TitleMessage!.Text);
            Assert.Contains($"{name} approved | 42 | {name} approved", audit.Logs);
            foreach (var result in new[] { audit, manual })
            {
                var effect = Assert.Single(result.Effects, candidate => candidate.Action is not null);
                Assert.Equal(approval.Identity, effect.Identity);
                Assert.Equal(LifecycleVerb.Read, effect.Verb);
                Assert.Equal(LockMode.Shared, effect.Mode);
                Assert.Equal("Audited", effect.Action!.Name);
                Assert.Equal(name + " approved", effect.Label);
                Assert.Null(effect.Data);
                Assert.Null(effect.DataSnapshot);
                Assert.All(result.TitleMessage!.Parts.Where(part => part.Resource.HasValue), part => Assert.Null(part.Snapshot));
            }
        }
    }

    private sealed class Capture : RunEventSink
    {
        public List<ScenarioFinished> Finished { get; } = [];
        protected override ValueTask OnScenarioFinishedAsync(ScenarioFinished e) { Finished.Add(e); return default; }
    }
}
