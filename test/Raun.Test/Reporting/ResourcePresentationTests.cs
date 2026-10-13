using System.ComponentModel;
using System.Text.Json;
using Raun.Model;
using Raun.Reporting.Html;
using Raun.Running;
using Xunit;

namespace Raun.Test.Reporting;

public sealed class ResourcePresentationTests
{
    [Fact]
    public void Every_object_reference_freezes_full_raw_data_and_uses_the_friendly_name()
    {
        var value = new Appointment { Id = "123", State = "before" };
        var formatting = new MessageFormattingContext(new ResourceIdentityResolver());
        var before = StepMessage.Create(formatting, $"{value} {value:id} {value:data}");
        value.State = "after";
        var after = StepMessage.Create(formatting, $"{value:id}");
        value.State = "later";

        Assert.Equal("Appointment:123 123 before", before.Text);
        Assert.All(before.Parts.Where(part => part.Resource.HasValue), part =>
        {
            Assert.Equal(new ResourceIdentity(typeof(Appointment), "123"), part.Resource);
            using var snapshot = JsonDocument.Parse(Assert.IsType<string>(part.Snapshot));
            Assert.Equal("123", snapshot.RootElement.GetProperty("Id").GetString());
            Assert.Equal("before", snapshot.RootElement.GetProperty("State").GetString());
        });
        using var latest = JsonDocument.Parse(Assert.IsType<string>(Assert.Single(after.Parts).Snapshot));
        Assert.Equal("after", latest.RootElement.GetProperty("State").GetString());
    }

    [Fact]
    public async Task Lifecycle_logs_keep_each_reference_while_effects_keep_their_existing_dedup_contract()
    {
        var value = new Appointment { Id = "123", State = "first" };
        var context = new ScenarioContext("step", "read appointment", null, CancellationToken.None);
        await context.Resources.Read(value);
        value.State = "second";
        await context.Resources.Read(value);
        value.State = "later";

        Assert.Equal(2, context.LogEntries.Count);
        var snapshots = context.LogEntries.Select(entry => Assert.Single(entry.Content!.Parts, part => part.Resource.HasValue).Snapshot).ToArray();
        using var first = JsonDocument.Parse(Assert.IsType<string>(snapshots[0]));
        using var second = JsonDocument.Parse(Assert.IsType<string>(snapshots[1]));
        Assert.Equal("first", first.RootElement.GetProperty("State").GetString());
        Assert.Equal("second", second.RootElement.GetProperty("State").GetString());
        var effect = Assert.Single(context.Resources.Effects);
        Assert.Same(value, effect.Data);
        using var recorded = JsonDocument.Parse(Assert.IsType<string>(effect.DataSnapshot));
        Assert.Equal("second", recorded.RootElement.GetProperty("State").GetString());
        Assert.All(context.LogEntries, entry => Assert.Contains("Appointment:123", entry.Message, StringComparison.Ordinal));

        var node = new ScenarioNode
        {
            Index = 0, StepId = "step", Phase = "When", OperationName = "read", DisplayNameTemplate = "read appointment", DependsOn = [],
            Invoke = (_, _) => Task.FromResult<object?>(null),
        };
        var definition = new ScenarioDefinition { ScenarioId = "sc", DisplayName = "scenario", MethodName = "N.Test", Nodes = [node] };
        var builder = new HtmlReportModelBuilder();
        builder.OnScenarioStarted(definition);
        builder.OnStepFinished(definition, new StepResult
        {
            Node = node, DisplayName = "read appointment", Status = StepStatus.Passed, StartedAt = effect.Timestamp,
            Effects = context.Resources.Effects, LogEntries = context.LogEntries,
        });
        var report = Assert.Single(builder.Build("now").Scenarios);
        var resource = Assert.Single(report.Resources);
        Assert.Equal("Appointment", resource.Type);
        Assert.Equal(typeof(Appointment).FullName, resource.QualifiedType);
        var reportStep = Assert.Single(report.Steps);
        using var firstLog = JsonDocument.Parse(Assert.IsType<string>(Assert.Single(reportStep.LogMessages[0].Parts, part => part.ResourceId is not null).Data));
        Assert.Equal("first", firstLog.RootElement.GetProperty("State").GetString());
        using var reportEffect = JsonDocument.Parse(Assert.IsType<string>(Assert.Single(reportStep.Effects).Data));
        Assert.Equal("second", reportEffect.RootElement.GetProperty("State").GetString());

    }

    [Fact]
    public void Raw_snapshot_has_no_inline_preview_length_limit()
    {
        var value = new Appointment { Id = "123", State = new string('x', 20_000) + "tail" };
        var message = StepMessage.Create(new MessageFormattingContext(new ResourceIdentityResolver()), $"{value:id}");
        using var snapshot = JsonDocument.Parse(Assert.IsType<string>(Assert.Single(message.Parts).Snapshot));
        Assert.Equal(value.State, snapshot.RootElement.GetProperty("State").GetString());
    }

    [Fact]
    public async Task Unserializable_resources_do_not_fail_the_step_and_mark_snapshot_unavailable()
    {
        var value = new Unserializable();
        var formatting = new MessageFormattingContext(new ResourceIdentityResolver());
        var part = Assert.Single(StepMessage.Create(formatting, $"{value:id}").Parts);
        Assert.NotNull(part.Resource);
        Assert.Null(part.Snapshot);
        var context = new ScenarioContext("step", "read", null, CancellationToken.None);
        await context.Resources.Read(value);
        var effect = Assert.Single(context.Resources.Effects);
        Assert.True(effect.DataSnapshotCaptured);
        Assert.Null(effect.DataSnapshot);
    }

    public sealed class Unserializable : IResource<Unserializable>
    {
        public string Broken => throw new InvalidOperationException(GetType().Name);
        public static ResourceKey KeyFor(Unserializable instance) => "key";
    }

    [Fact]
    public void Data_format_omits_the_record_type_prefix_but_keeps_the_resource_reference()
    {
        var value = new AppointmentDto("123", "Alice");
        var part = Assert.Single(StepMessage.Create(new MessageFormattingContext(new ResourceIdentityResolver()), $"{value:data}").Parts);
        Assert.Equal("{ Id = 123, Patient = Alice }", part.Text);
        Assert.Equal(new ResourceIdentity(typeof(AppointmentDto), "123"), part.Resource);
        Assert.NotNull(part.Snapshot);
    }

    [Fact]
    public void Data_format_also_omits_a_custom_friendly_type_prefix()
    {
        var value = new FriendlyAppointment("123");
        var part = Assert.Single(StepMessage.Create(new MessageFormattingContext(new ResourceIdentityResolver()), $"{value:data}").Parts);
        Assert.Equal("{ Id = 123 }", part.Text);
        Assert.Equal(new ResourceIdentity(typeof(FriendlyAppointment), "123"), part.Resource);
    }

    [DisplayName("Appointment")]
    public sealed record FriendlyAppointment(string Id) : IResource<FriendlyAppointment>
    {
        public static ResourceKey KeyFor(FriendlyAppointment instance) => instance.Id;
        public override string ToString() => $"Appointment {{ Id = {Id} }}";
    }

    [DisplayName("Appointment")]
    public sealed record AppointmentDto(string Id, string Patient) : IResource<AppointmentDto>
    {
        public static ResourceKey KeyFor(AppointmentDto instance) => instance.Id;
    }

    [DisplayName("Appointment")]
    public sealed class Appointment : IResource<Appointment>
    {
        public required string Id { get; init; }
        public required string State { get; set; }
        public static ResourceKey KeyFor(Appointment instance) => instance.Id;
        public override string ToString() => State;
    }
}
