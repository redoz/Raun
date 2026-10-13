using System.Globalization;
using Xunit;

namespace Raun.Test.Reporting;

public sealed class StepMessageTests
{
    private static MessageFormattingContext Context() => new(new ResourceIdentityResolver(), CultureInfo.InvariantCulture);

    [Fact]
    public void Ordinary_interpolation_keeps_resources_and_formats_each_policy()
    {
        var appointment = new Appointment("123", "seed");
        var message = StepMessage.Create(Context(), $"Using {appointment}; {appointment:id}; {appointment:data}; {42:000}");
        Assert.Equal("Using Appointment:123; 123; seed; 042", message.Text);
        var resources = message.Parts.Where(p => p.Resource.HasValue).ToArray();
        Assert.Equal(3, resources.Length);
        Assert.All(resources, p => Assert.Equal(new ResourceIdentity(typeof(Appointment), "123"), p.Resource));
        Assert.Null(resources[0].Data);
        Assert.Equal("seed", resources[2].Data);
        Assert.Null(message.Parts[^1].Resource);
    }

    [Fact]
    public void Null_and_ordinary_values_keep_alignment_and_formats()
    {
        Appointment? missing = null;
        var message = StepMessage.Create(Context(), $"[{missing}] [{42,5:000}] [{42,-5:000}] [{1.25:0.00}]");
        Assert.Equal("[] [  042] [042  ] [1.25]", message.Text);
        Assert.All(message.Parts, p => Assert.Null(p.Resource));
    }

    [Fact]
    public void Resource_alignment_and_unsupported_format_keep_identity()
    {
        var appointment = new Appointment("123", "seed");
        var message = StepMessage.Create(Context(), $"[{appointment,5:id}] [{appointment,-7:data}] [{appointment:custom}]");
        Assert.Equal("[  123] [seed   ] [seed]", message.Text);
        Assert.Equal(3, message.Parts.Count(p => p.Resource.HasValue));
    }

    [Fact]
    public void Object_typed_resource_remains_ordinary_until_declared_selector_is_registered()
    {
        var resolver = new ResourceIdentityResolver();
        var context = new MessageFormattingContext(resolver);
        object value = new Appointment("123", "seed");
        var ordinary = StepMessage.Create(context, $"{value}");
        Assert.Equal("seed", ordinary.Text);
        Assert.Null(Assert.Single(ordinary.Parts).Resource);
        resolver.Identify<object>(_ => "object-key");
        var resource = StepMessage.Create(context, $"{value}");
        Assert.Equal("Object:object-key", resource.Text);
        Assert.Equal(new ResourceIdentity(typeof(object), "object-key"), Assert.Single(resource.Parts).Resource);
    }

    [Fact]
    public void Nullable_value_resource_uses_only_its_declared_selector()
    {
        var resolver = new ResourceIdentityResolver();
        var context = new MessageFormattingContext(resolver);
        ValueResource? value = new ValueResource(42);
        var plain = StepMessage.Create(context, $"{value}");
        Assert.Equal("ordinary", plain.Text);
        Assert.Null(Assert.Single(plain.Parts).Resource);
        resolver.Identify<ValueResource?>(_ => "declared");
        var selected = StepMessage.Create(context, $"{value:id}");
        Assert.Equal("declared", selected.Text);
        Assert.Equal(new ResourceIdentity(typeof(ValueResource?), "declared"), Assert.Single(selected.Parts).Resource);
    }

    [Fact]
    public void Explicit_formattable_string_keeps_runtime_resources_escaped_braces_and_invariant_numbers()
    {
        var context = new MessageFormattingContext(new ResourceIdentityResolver(), CultureInfo.GetCultureInfo("nl-NL"));
        object value = new Appointment("123", "seed");
        FormattableString input = $"{{value}} {value:id} [{42,5:000}] {1.25:0.00}";
        var message = StepMessage.Create(context, input);
        Assert.Equal("{value} 123 [  042] 1.25", message.Text);
        Assert.Equal(new ResourceIdentity(typeof(Appointment), "123"), Assert.Single(message.Parts, p => p.Resource.HasValue).Resource);
        Assert.Equal("1,25", StepMessage.Create(context, $"{1.25:0.00}").Text);
    }

    [Fact]
    public void Builder_and_handler_capture_frozen_snapshots()
    {
        var appointment = new Appointment("123", "seed");
        var builder = new StepMessageBuilder(Context()).AppendLiteral("Using ").AppendFormatted(appointment, format: "data");
        var message = builder.Build();
        appointment.Data = "changed";
        Assert.Equal("Using seed", message.Text);
        Assert.Equal("seed", message.Parts[1].Data);
        var captured = StepMessage.Create(Context(), $"{appointment:data}");
        appointment.Data = "later";
        Assert.Equal("changed", Assert.Single(captured.Parts).Data);
    }

    [Fact]
    public void Throwing_data_keeps_identity_and_does_not_retry_ToString()
    {
        var value = new ThrowingData();
        var message = StepMessage.Create(Context(), $"{value:data}");
        Assert.Equal("data unavailable", message.Text);
        Assert.Equal(new ResourceIdentity(typeof(ThrowingData), "123"), Assert.Single(message.Parts).Resource);
        Assert.Null(Assert.Single(message.Parts).Data);
        Assert.Equal(1, value.Calls);
    }

    [Fact]
    public void Invalid_selector_identity_uses_type_name_with_alignment_without_data_retry()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<ThrowingData>(_ => default);
        var value = new ThrowingData();
        var message = StepMessage.Create(new MessageFormattingContext(resolver), $"[{value,16:id}] {value:data}");
        Assert.Equal("[    ThrowingData] ThrowingData", message.Text);
        Assert.All(message.Parts, p => Assert.Null(p.Resource));
        Assert.Equal(0, value.Calls);
    }

    [Fact]
    public void Invalid_supplied_identity_uses_declared_type_name_without_reference()
    {
        var nullKey = new ResourceIdentity(typeof(Appointment), default);
        ResourceIdentity missing = default;
        var message = StepMessage.Create(Context(), $"[{nullKey,18:id}] {missing:id} {missing:data}");
        Assert.Equal("[  ResourceIdentity] ResourceIdentity ResourceIdentity", message.Text);
        Assert.All(message.Parts, p => Assert.Null(p.Resource));
    }

    [Fact]
    public void Throwing_key_uses_type_name_without_calling_data()
    {
        var value = new ThrowingKey();
        var message = StepMessage.Create(Context(), $"{value:data}");
        Assert.Equal("ThrowingKey", message.Text);
        Assert.Null(Assert.Single(message.Parts).Resource);
        Assert.Equal(0, value.Calls);
    }

    [Fact]
    public void Snapshot_and_inline_limits_preserve_surrogates_and_line_breaks()
    {
        var value = new Appointment("123", new string('a', 158) + "😀" + "\n" + new string('b', 16222) + "😀tail");
        var part = Assert.Single(StepMessage.Create(Context(), $"{value:data}").Parts);
        Assert.True(part.DataTruncated);
        Assert.NotNull(part.Data);
        Assert.Equal(16383, part.Data.Length);
        Assert.False(char.IsHighSurrogate(part.Data[^1]));
        Assert.Equal(new string('a', 158) + "…", part.Text);
        Assert.DoesNotContain("\n", part.Text, StringComparison.Ordinal);
        Assert.Contains("\n", part.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_preview_is_single_line_and_counts_ellipsis_inside_limit()
    {
        var value = new Appointment("123", "first\r\nsecond\nthird\rfourth");
        var part = Assert.Single(StepMessage.Create(Context(), $"{value:data}").Parts);
        Assert.Equal("first second third fourth", part.Text);
        Assert.Equal(value.Data, part.Data);
        Assert.False(part.DataTruncated);
        value.Data = new string('x', 161);
        part = Assert.Single(StepMessage.Create(Context(), $"{value:data}").Parts);
        Assert.Equal(new string('x', 159) + "…", part.Text);
        Assert.Equal(160, part.Text.Length);
        Assert.False(part.DataTruncated);
    }

    [Fact]
    public void Message_copies_input_parts_and_plain_text_is_unstructured()
    {
        var parts = new List<StepMessagePart> { new("first") };
        var message = new StepMessage(parts);
        parts[0] = new("changed");
        Assert.Equal("first", message.Text);
        Assert.Equal("plain", StepMessage.FromText("plain").Text);
        Assert.Equal("plain", StepMessage.Create(Context(), "plain").Text);
        Assert.Null(Assert.Single(StepMessage.FromText("plain").Parts).Resource);
    }

    [Fact]
    public void Identity_value_keeps_actual_identity_without_data_capture()
    {
        var identity = new ResourceIdentity(typeof(Appointment), "123");
        var message = StepMessage.Create(Context(), $"{identity} {identity:id} {identity:data}");
        Assert.Equal("Appointment:123 123 Appointment:123", message.Text);
        Assert.All(message.Parts.Where(p => p.Resource.HasValue), p => { Assert.Equal(identity, p.Resource); Assert.Null(p.Data); });
    }

    [Fact]
    public void Span_interpolation_keeps_text_alignment_and_mixed_resource_parts()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<string>(_ => "string-key");
        var context = new MessageFormattingContext(resolver);
        var buffer = "seed".ToCharArray();
        ReadOnlySpan<char> span = buffer;
        var appointment = new Appointment("123", "data");
        var message = StepMessage.Create(context, $"[{span}] [{span,6}] [{span,-6:ignored}] [{span:ignored}] {appointment:id}");
        buffer[0] = 'X';
        Assert.Equal("[seed] [  seed] [seed  ] [seed] 123", message.Text);
        Assert.Equal(new ResourceIdentity(typeof(Appointment), "123"), Assert.Single(message.Parts, p => p.Resource.HasValue).Resource);
        Assert.All(message.Parts.Where(p => p.Text.Contains("seed", StringComparison.Ordinal)), p => Assert.Null(p.Resource));
    }

    [Fact]
    public void Span_handler_and_builder_snapshot_buffers_at_append_time()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<string>(_ => "string-key");
        var context = new MessageFormattingContext(resolver);
        var buffer = "seed".ToCharArray();
        ReadOnlySpan<char> span = buffer;
        var handler = new StepMessageInterpolatedStringHandler(0, 1);
        handler.AppendFormatted(span, 6, "ignored");
        var builder = new StepMessageBuilder(context).AppendFormatted(span, -6, "ignored");
        buffer[0] = 'X';
        var captured = handler.Materialize(context);
        var built = builder.Build();
        Assert.Equal("  seed", captured.Text);
        Assert.Equal("seed  ", built.Text);
        Assert.Null(Assert.Single(captured.Parts).Resource);
        Assert.Null(Assert.Single(built.Parts).Resource);
    }

    [Fact]
    public void Materialization_releases_shared_capture_buffer()
    {
        var handler = new StepMessageInterpolatedStringHandler(0, 1);
        handler.AppendFormatted(new Appointment("123", "seed"), "data");
        var copy = handler;
        Assert.Equal("seed", handler.Materialize(Context()).Text);
        Assert.Empty(copy.Materialize(Context()).Parts);
    }

    public sealed class Appointment(string id, string data) : IResource<Appointment>
    {
        public string Id { get; } = id;
        public string Data { get; set; } = data;
        public static ResourceKey KeyFor(Appointment value) => value.Id;
        public override string ToString() => Data;
    }

    public readonly record struct ValueResource(int Id) : IResource<ValueResource>
    {
        public static ResourceKey KeyFor(ValueResource value) => "value-key";
        public override string ToString() => "ordinary";
    }

    public sealed class ThrowingData : IResourceIdentity
    {
        public int Calls { get; private set; }
        public ResourceKey GetResourceKey() => "123";
        // Deliberately hostile user formatting verifies evidence cannot fail the run.
#pragma warning disable CA1065
        public override string ToString() { Calls++; throw new InvalidOperationException("no data"); }
#pragma warning restore CA1065
    }

    public sealed class ThrowingKey : IResourceIdentity
    {
        public int Calls { get; private set; }
        public ResourceKey GetResourceKey() => throw new InvalidOperationException("no key");
        public override string ToString() { Calls++; return "should not run"; }
    }
}
