using System.ComponentModel;
using System.Text.Json;
using Xunit;

namespace Raun.Test.Resources;

public class ResourceLabelTests
{
    public sealed class Booking : IResource<Booking>
    {
        public string Id { get; init; } = "123";
        public string Name { get; set; } = "Jane";
        public static ResourceKey KeyFor(Booking value) => value.Id;
        public static string? LabelFor(Booking value) => value.Name;
    }

    [Fact]
    public void Every_format_freezes_its_label_identity_and_snapshot()
    {
        var context = new ScenarioContext("s", "step", null, default);
        var booking = new Booking();
        context.Log($"Booked {booking} / {booking:id} / {booking:data}");
        booking.Name = "Changed";
        var entry = Assert.Single(context.LogEntries);
        Assert.StartsWith("Booked Jane / 123 / ", entry.Message, StringComparison.Ordinal);
        var parts = entry.Content!.Parts.Where(part => part.Resource.HasValue).ToArray();
        Assert.Equal(3, parts.Length);
        Assert.All(parts, part =>
        {
            Assert.Equal("Jane", part.Label);
            Assert.Equal(new ResourceIdentity(typeof(Booking), "123"), part.Resource);
            using var snapshot = JsonDocument.Parse(Assert.IsType<string>(part.Snapshot));
            Assert.Equal("Jane", snapshot.RootElement.GetProperty("Name").GetString());
        });
    }

    [Fact]
    public void Registered_selector_overrides_type_label_without_changing_the_key()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Label<Booking>(_ => "Test booking");
        var message = StepMessage.Create(new MessageFormattingContext(resolver), $"{new Booking()}");
        Assert.Equal("Test booking", message.Text);
        Assert.Equal("123", Assert.Single(message.Parts).Resource!.Value.Key.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Blank_registered_labels_fall_through_to_the_type_label(string? label)
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Label<Booking>(_ => label);
        Assert.Equal("Jane", StepMessage.Create(new MessageFormattingContext(resolver), $"{new Booking()}").Text);
    }

    [DisplayName("Appointment")]
    public sealed record Unlabelled(string Id) : IResource<Unlabelled>
    {
        public static ResourceKey KeyFor(Unlabelled value) => value.Id;
    }

    [Fact]
    public void Omitted_label_uses_type_display_name_and_key()
        => Assert.Equal("Appointment:42", StepMessage.Create(
            new MessageFormattingContext(new ResourceIdentityResolver()), $"{new Unlabelled("42")}").Text);

    public interface IEntity { string Id { get; } }
    public sealed record Entity(string Id) : IEntity;

    [Fact]
    public async Task Interface_selectors_use_the_same_identity_in_messages_and_effects()
    {
        var context = new ScenarioContext("s", "step", null, default);
        context.Resources.Identify<IEntity>(entity => entity.Id);
        context.Resources.Label<IEntity>(_ => "Entity label");
        IEntity entity = new Entity("42");
        await context.Resources.Read(entity);
        context.Log($"Read {entity}");
        Assert.Equal("Read Entity label", context.Logs[1]);
        var part = Assert.Single(context.LogEntries[1].Content!.Parts, p => p.Resource.HasValue);
        Assert.Equal(Assert.Single(context.Resources.Effects).Identity, part.Resource);
    }

    [Fact]
    public async Task Lifecycle_labels_are_captured_before_later_mutations()
    {
        var context = new ScenarioContext("s", "step", null, default);
        var booking = new Booking();
        await context.Resources.Read(booking);
        booking.Name = "Changed";
        Assert.Equal("[resource] Read Jane", Assert.Single(context.Logs));
        Assert.Equal("Jane", Assert.Single(context.Resources.Effects).Label);
    }

    [Fact]
    public void Snapshot_keeps_unicode_and_punctuation_readable()
    {
        var booking = new Booking { Name = "Åsa + Zoë <test>" };
        var message = StepMessage.Create(new MessageFormattingContext(new ResourceIdentityResolver()), $"{booking:id}");
        Assert.Contains("Åsa + Zoë <test>", Assert.Single(message.Parts).Snapshot, StringComparison.Ordinal);
    }
}
