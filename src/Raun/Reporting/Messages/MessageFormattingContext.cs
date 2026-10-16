namespace Raun;

/// <summary>Explicit identity and culture services used while capturing a message.</summary>
public sealed class MessageFormattingContext
{
    public MessageFormattingContext(ResourceIdentityResolver resolver, IFormatProvider? formatProvider = null)
        : this(resolver, formatProvider, ResourceLabelScope.Standalone()) { }

    private MessageFormattingContext(ResourceIdentityResolver resolver, IFormatProvider? formatProvider, ResourceLabelScope labels)
    {
        Labels = labels;
        ArgumentNullException.ThrowIfNull(resolver);
        Resolver = resolver;
        FormatProvider = formatProvider;
    }

    internal ResourceLabelScope Labels { get; }
    internal MessageFormattingContext ForStep(int index) => new(Resolver, FormatProvider, Labels.ForStep(index));
    internal MessageFormattingContext WithFormatProvider(IFormatProvider? provider) => new(Resolver, provider, Labels);
    internal MessageFormattingContext WithLabelMemory(IReadOnlyList<Model.ScenarioNode> nodes)
        => new(Resolver, FormatProvider, new ResourceLabelMemory(nodes).ForStep(-1));

    public ResourceIdentityResolver Resolver { get; }
    public IFormatProvider? FormatProvider { get; }
}
