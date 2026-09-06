namespace Raun.Mtp.Test;

// Public top-level on purpose: a private nested token would trip CA1812, a public nested one CA1034.

[ExclusiveResource]
public sealed class ExclusiveDb : IContendedResource;

[SharedResource]
public sealed class SharedCatalog : IContendedResource;

[PooledResource(2)]
public sealed class PooledSmtp : IContendedResource;
