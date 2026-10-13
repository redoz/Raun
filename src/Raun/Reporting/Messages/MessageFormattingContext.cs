namespace Raun;

/// <summary>Explicit identity and culture services used while capturing a message.</summary>
public sealed class MessageFormattingContext
{
    public MessageFormattingContext(ResourceIdentityResolver resolver, IFormatProvider? formatProvider = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        Resolver = resolver;
        FormatProvider = formatProvider;
    }

    public ResourceIdentityResolver Resolver { get; }
    public IFormatProvider? FormatProvider { get; }
}
