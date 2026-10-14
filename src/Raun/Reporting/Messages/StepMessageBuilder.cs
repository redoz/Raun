namespace Raun;

/// <summary>Materializes resource-aware message parts without retaining source values.</summary>
public sealed class StepMessageBuilder
{
    private readonly MessageFormattingContext _context;
    private readonly List<StepMessagePart> _parts = [];

    public StepMessageBuilder(MessageFormattingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public StepMessageBuilder AppendLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0)
        {
            _parts.Add(new(value));
        }
        return this;
    }

    public StepMessageBuilder AppendFormatted<T>(T value, int alignment = 0, string? format = null)
        => AppendFormatted(typeof(T), value, alignment, format);

    public StepMessageBuilder AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null)
        => AppendPlainText(value.ToString(), alignment);

    internal StepMessageBuilder AppendPlainText(string value, int alignment)
        => AppendPart(new(value), alignment);

    internal StepMessageBuilder AppendFormatted(Type declaredType, object? value, int alignment, string? format)
    {
        var part = Format(declaredType, value, format);
        if (part.Resource.HasValue)
        {
            part = part with { Snapshot = ResourcePresentation.Capture(value) };
        }
        return AppendPart(part, alignment);
    }

    private StepMessageBuilder AppendPart(StepMessagePart part, int alignment)
    {
        var text = alignment < 0 ? part.Text.PadRight(-alignment) : part.Text.PadLeft(alignment);
        _parts.Add(part with { Text = text });
        return this;
    }

    public StepMessage Build() => new(_parts);

    private StepMessagePart Format(Type declaredType, object? value, string? format)
    {
        if (value is null)
        {
            return new(string.Empty);
        }

        ResourceIdentity? identity;
        try
        {
            identity = _context.Resolver.TryResolve(declaredType, value, out var resolved) ? resolved : null;
        }
        catch (Exception)
        {
            // User key selectors must never fail a scenario or trigger a second ToString attempt.
            return new(declaredType.Name);
        }

        try
        {
            if (identity is { } resource)
            {
                if (resource.Type is null || resource.Key.Value is null)
                {
                    // Default public identity/key structs cannot form usable report references.
                    return new(declaredType.Name);
                }
                var label = value is ResourceIdentity ? ResourcePresentation.Display(resource)
                    : _context.Resolver.ResolveLabel(resource, value);
                if (string.IsNullOrEmpty(format))
                {
                    return new(label, resource) { Label = label };
                }
                if (string.Equals(format, "id", StringComparison.Ordinal))
                {
                    return new(resource.Key.ToString(), resource) { Label = label };
                }
                if (string.Equals(format, "data", StringComparison.Ordinal))
                {
                    if (value is ResourceIdentity)
                    {
                        return new(ResourcePresentation.Display(resource), resource) { Label = label };
                    }
                    var data = ResourcePresentation.Data(value);
                    var truncated = data.Length > 16_384;
                    data = Prefix(data, 16_384);
                    var inline = data.Replace("\r\n", " ", StringComparison.Ordinal)
                        .Replace('\r', ' ').Replace('\n', ' ');
                    if (inline.Length > 160 || truncated)
                    {
                        inline = Prefix(inline, 159) + "…";
                    }
                    return new(inline, resource, data, truncated) { Label = label };
                }
            }

            var text = Ordinary(value, format);
            return new(text, identity);
        }
        catch (Exception)
        {
            // Formatting is evidence only. Keep an already-resolved reference without retrying.
            return new(identity.HasValue ? "data unavailable" : declaredType.Name, identity);
        }
    }

    private string Ordinary(object value, string? format)
    {
        if (_context.FormatProvider?.GetFormat(typeof(ICustomFormatter)) is ICustomFormatter custom)
        {
            var result = custom.Format(format, value, _context.FormatProvider);
            if (result is not null)
            {
                return result;
            }
        }
        return value is IFormattable formattable
            ? formattable.ToString(format, _context.FormatProvider) ?? string.Empty
            : value.ToString() ?? string.Empty;
    }

    private static string Prefix(string value, int limit)
    {
        if (value.Length <= limit)
        {
            return value;
        }
        if (limit > 0 && char.IsHighSurrogate(value[limit - 1]) && char.IsLowSurrogate(value[limit]))
        {
            limit--;
        }
        return value[..limit];
    }
}
