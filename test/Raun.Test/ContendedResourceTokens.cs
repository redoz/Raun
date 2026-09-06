namespace Raun.Test;

// Public top-level on purpose: a private nested token would trip CA1812 (never instantiated) and a
// public nested one CA1034. They are names, never instances — see the concurrent-scenarios design.

[ExclusiveResource]
public sealed class ExclusiveDb : IContendedResource;

[SharedResource]
public sealed class SharedCatalog : IContendedResource;

[PooledResource(2)]
public sealed class PooledSmtp : IContendedResource;
