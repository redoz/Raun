using System.Globalization;
using System.Text;

namespace Raun;

/// <summary>A frozen resource-aware message and its plain-text projection.</summary>
public sealed class StepMessage
{
    public StepMessage(IEnumerable<StepMessagePart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        Parts = Array.AsReadOnly(parts.ToArray());
        Text = string.Concat(Parts.Select(part => part.Text));
    }

    public IReadOnlyList<StepMessagePart> Parts { get; }
    public string Text { get; }

    public static StepMessage FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new([new StepMessagePart(text)]);
    }

    public static StepMessage Create(MessageFormattingContext context, string text)
    {
        ArgumentNullException.ThrowIfNull(context);
        return FromText(text);
    }

    public static StepMessage Create(MessageFormattingContext context, StepMessageInterpolatedStringHandler message)
        => message.Materialize(context);

    public static StepMessage Create(MessageFormattingContext context, FormattableString message)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(message);
        var builder = new StepMessageBuilder(new(context.Resolver, CultureInfo.InvariantCulture));
        var format = message.Format;
        _ = CompositeFormat.Parse(format); // Validate the platform's composite-format grammar first.
        var literal = new StringBuilder();
        for (var i = 0; i < format.Length;)
        {
            var ch = format[i++];
            if (ch == '{' && format[i] != '{')
            {
                builder.AppendLiteral(literal.ToString());
                literal.Clear();
                var start = i;
                while (char.IsAsciiDigit(format[i]))
                {
                    i++;
                }
                var index = int.Parse(format.AsSpan(start, i - start), CultureInfo.InvariantCulture);
                while (format[i] == ' ')
                {
                    i++;
                }
                var alignment = 0;
                if (format[i] == ',')
                {
                    i++;
                    start = i;
                    while (format[i] != ':' && format[i] != '}')
                    {
                        i++;
                    }
                    alignment = int.Parse(format.AsSpan(start, i - start), CultureInfo.InvariantCulture);
                }
                string? valueFormat = null;
                if (format[i] == ':')
                {
                    start = ++i;
                    while (format[i] != '}')
                    {
                        i++;
                    }
                    valueFormat = format[start..i];
                }
                i++;
                var value = message.GetArgument(index);
                builder.AppendFormatted(value?.GetType() ?? typeof(object), value, alignment, valueFormat);
            }
            else
            {
                literal.Append(ch);
                if (ch is '{' or '}')
                {
                    i++;
                }
            }
        }
        builder.AppendLiteral(literal.ToString());
        return builder.Build();
    }
}
