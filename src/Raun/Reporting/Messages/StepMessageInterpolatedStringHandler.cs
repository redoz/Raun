using System.Runtime.CompilerServices;

namespace Raun;

/// <summary>Captures interpolation until explicit message formatting services are available.</summary>
// A single-use interpolation capture has buffer ownership, not value equality.
#pragma warning disable CA1815
[InterpolatedStringHandler]
public struct StepMessageInterpolatedStringHandler
{
    private List<Capture>? _captures;

    public StepMessageInterpolatedStringHandler(int literalLength, int formattedCount)
    {
        _captures = new List<Capture>(formattedCount + 1);
    }

    public void AppendLiteral(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        (_captures ??= []).Add(new(value, null, null, 0, null));
    }

    public void AppendFormatted<T>(T value) => AppendFormatted(value, 0, null);
    public void AppendFormatted<T>(T value, string? format) => AppendFormatted(value, 0, format);
    public void AppendFormatted<T>(T value, int alignment) => AppendFormatted(value, alignment, null);
    public void AppendFormatted<T>(T value, int alignment, string? format)
        => (_captures ??= []).Add(new(null, typeof(T), value, alignment, format));

    public void AppendFormatted(ReadOnlySpan<char> value) => AppendFormatted(value, 0, null);
    public void AppendFormatted(ReadOnlySpan<char> value, string? format) => AppendFormatted(value, 0, format);
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment) => AppendFormatted(value, alignment, null);
    public void AppendFormatted(ReadOnlySpan<char> value, int alignment, string? format)
        // An ordinary struct cannot keep the span; freeze its text at capture time.
        => (_captures ??= []).Add(new(null, typeof(ReadOnlySpan<char>), value.ToString(), alignment, format));

    public StepMessage Materialize(MessageFormattingContext context)
    {
        try
        {
            var builder = new StepMessageBuilder(context);
            if (_captures is not null)
            {
                foreach (var capture in _captures)
                {
                    if (capture.Literal is { } literal)
                    {
                        builder.AppendLiteral(literal);
                    }
                    else if (capture.DeclaredType == typeof(ReadOnlySpan<char>))
                    {
                        // Span text is ordinary evidence even when a string selector is registered.
                        builder.AppendPlainText((string)capture.Value!, capture.Alignment);
                    }
                    else
                    {
                        builder.AppendFormatted(capture.DeclaredType!, capture.Value, capture.Alignment, capture.Format);
                    }
                }
            }
            return builder.Build();
        }
        finally
        {
            // Struct copies share the buffer: clearing also releases values held by those copies.
            _captures?.Clear();
            _captures = null;
        }
    }

    private readonly record struct Capture(string? Literal, Type? DeclaredType, object? Value, int Alignment, string? Format);
}
#pragma warning restore CA1815
