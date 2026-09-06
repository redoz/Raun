namespace Raun.Mtp.Test;

// Public top-level on purpose: a private nested token would trip CA1812, a public nested one CA1034.

[ExclusiveResource]
public sealed class ExclusiveDb : IContendedResource;

[SharedResource]
public sealed class SharedCatalog : IContendedResource;

[PooledResource(2)]
public sealed class PooledSmtp : IContendedResource;

/// <summary>Invalid on purpose: no kind attribute. Exercises the runtime gate's rejection path, which RAUN015 exists to catch at compile time.</summary>
#pragma warning disable RAUN015 // Deliberately mis-declared: the run-loop test needs a token the gate refuses at run time.
public sealed class MisdeclaredToken : IContendedResource;
#pragma warning restore RAUN015
