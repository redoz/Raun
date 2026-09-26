# Scenario Resources C1 — Effects & Tracing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a DSL step declare symbolic resource roles (`[Creates]`/`[Loads]`/`[Reads]`/`[Edits]`/`[Deletes]`) that the source generator lowers to an imperative `ctx.Resources.*` API, producing a per-step `ResourceEffect` trace stream that flows out through the existing `StepResult` / `IStepObserver` channel — with **no locking or scheduling changes** (those are C2).

**Architecture:** A new runner-neutral resource subsystem under `src/Raun/Resources/` provides typed identity (`IResource<TSelf>` CRTP, `IResourceIdentity`, a registered selector, and value-equality fallback — resolved by a 4-link `ResourceIdentityResolver`), an imperative `ResourceContext` hanging off `ScenarioContext.Resources` whose verbs record deduped `ResourceEffect`s, and a `StepResult.Effects` collection the scheduler fills from the context (exactly as it already does for `Logs`/`Attachments`). The generator reads per-parameter and per-return role attributes, raises **RAUN009** when a resource-typed parameter/return has no role, and injects `await __ctx.Resources.<Verb>(…)` calls into the generated invoke lambda. The MTP reporter surfaces effects as standard output so the stream is visible end-to-end.

**Tech Stack:** C# (`net10.0`, `LangVersion=latest` — static-abstract interface members available), Roslyn incremental generator + analyzer (`Microsoft.CodeAnalysis` 5.3), xUnit v3 + Verify for tests, Jujutsu (`jj`) for commits. Repo builds with `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-all`, `EnforceCodeStyleInBuild=true`.

---

## Conventions for every task (read once, apply throughout)

- **TDD, green throughout.** Write the failing test first, run it, watch it fail for the *expected* reason, implement the minimum, run it green, then commit. Keep the whole build warning-clean (warnings are errors).
- **Commits via `jj`** (see memory `working-style`): finish a task by naming the working copy then opening the next:
  ```bash
  jj describe -m "feat(resources): <subject>"
  jj new
  ```
  **No `Co-Authored-By` or tooling trailers** (memory `commit-style`) — subject + optional body only.
- **Test commands** (xUnit projects, standard VSTest filter):
  - Core: `dotnet test test/Raun.Test/Raun.Test.csproj`
  - Generator/analyzer: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj`
  - A single test: append `--filter "FullyQualifiedName~<ClassOrMethod>"`.
  - Whole build: `dotnet build` (0 warnings, 0 errors is the bar).
  - The `-- --filter-class/--filter-method` (MTP) syntax is **only** for running the `samples/AppointmentTests` app, not these xUnit test projects (Task 11).
- **Warning-clean gotcha (`EnforceCodeStyleInBuild` + IDE0079):** do **not** pre-emptively add `#pragma warning disable` — an unnecessary suppression itself errors as IDE0079. Add a suppression *only after* a build proves the warning fires, with a one-line justification (the repo precedent is the CA1040 pragma on an empty marker interface). The most likely candidate here is **CA1000** on `IResource<TSelf>.KeyFor` (static member on a generic type) — handle it reactively in Task 2 if it appears.
- **Namespaces** (folders are organizational; the repo does *not* align folder→namespace, e.g. `Attributes/StepNameAttribute.cs` is namespace `Raun`):
  - `Raun.Model` — `ResourceKey`, `ResourceIdentity`, `LifecycleVerb`, `ResourceEffect` (data model, next to `StepResult`).
  - `Raun` — `IResource<TSelf>`, `IResourceIdentity`, `ResourceIdentityResolver`, `ResourceContext`, and the role attributes (author-facing, reached via `using Raun;` like `[StepName]`).
- **C2 is explicitly out of scope:** no locking, no `ScenarioLockScope`/`BeginScenarioScope`/`LockAsync`, no `Access`/`LockMode`, no wound-wait, no scheduler/session changes, no static claim catalog, and **no** `[Resource]`/`[ResourceKey]` key-projection codegen, `ISingletonResource<T>`, or `[Requires<T>]`. C1 identity is fully covered by `IResource<TSelf>` (CRTP), `IResourceIdentity`, a registered selector, and value-equality. Verbs are `async` (return `ValueTask`) now so the surface is stable when C2 makes acquisition genuinely await a lock.

---

## File Structure

**New files (all under `src/Raun/Resources/`):**

| File | Namespace | Responsibility |
|---|---|---|
| `ResourceKey.cs` | `Raun.Model` | Value-equality key wrapper; implicit from `string`; `FromValue(object)`. |
| `ResourceIdentity.cs` | `Raun.Model` | `(Type, ResourceKey)` identity; `ToString()` → `Type:Key`. |
| `LifecycleVerb.cs` | `Raun.Model` | `enum { Read, Load, Create, Edit, Delete }` — numeric order **is** strength. |
| `ResourceEffect.cs` | `Raun.Model` | One trace event: verb, identity, data snapshot, owning step, timestamp. |
| `IResource.cs` | `Raun` | `IResource<TSelf>` CRTP marker with `static abstract ResourceKey KeyFor(TSelf)`; `IResourceIdentity` (runtime key). |
| `ResourceIdentityResolver.cs` | `Raun` | 4-link identity chain + selector registration. |
| `ResourceContext.cs` | `Raun` | `ctx.Resources` — the imperative verbs; records deduped effects per step. |
| `ResourceRoleAttributes.cs` | `Raun` | `[Creates]`, `[Loads]`, `[Reads]`, `[Edits]`, `[Deletes]`. |

**Modified files:**

| File | Change |
|---|---|
| `src/Raun/ScenarioContext.cs` | Add lazy `Resources` property (resolver from `Services`, else empty). |
| `src/Raun/Model/StepResult.cs` | Add `IReadOnlyList<ResourceEffect> Effects`. |
| `src/Raun/Scheduling/ScenarioScheduler.cs` | Copy `context.Resources.Effects` into passed/failed `StepResult`s. |
| `src/Raun.Generator/Lowering/Ir.cs` | Add `ResourceClaim` record + `ResourceClaims` on `ParsedStep`. |
| `src/Raun.Generator/Lowering/AttributeReader.cs` | Add `ParameterRole` / `ReturnRole`. |
| `src/Raun.Generator/Lowering/ScenarioParser.cs` | Populate `ResourceClaims` in `BuildStep`. |
| `src/Raun.Generator/Emit/ScenarioEmitter.cs` | Inject `await __ctx.Resources.<Verb>(…)` into the invoke lambda. |
| `src/Raun.Generator/Analysis/Descriptors.cs` | Add `MissingResourceRole` (RAUN009). |
| `src/Raun.Generator/Analysis/ScenarioAnalyzer.cs` | Register RAUN009 + emit it for unannotated resource params/returns. |
| `src/Raun.Generator/AnalyzerReleases.Unshipped.md` | Add the RAUN009 row. |
| `src/Raun.Mtp/RaunStepReporter.cs` | Surface effects as standard output. |
| `samples/AppointmentTests/AppointmentDsl.cs` + `Scenarios.cs` | Exercise roles end-to-end. |

**New test files / additions:**

- `test/Raun.Test/Resources/ResourceKeyTests.cs`, `ResourceIdentityResolverTests.cs`, `ResourceContextTests.cs`
- additions to `test/Raun.Test/ScenarioContextTests.cs`, `SchedulerTests.cs`
- `test/Raun.Generator.Test/ResourceLoweringTests.cs`, additions to `AnalyzerTests.cs`, `SampleSources.cs`, a new snapshot
- additions to `test/Raun.Mtp.Test/RaunStepReporterTests.cs`

---

### Task 1: Resource value types (`ResourceKey`, `ResourceIdentity`, `LifecycleVerb`)

**Files:**
- Create: `src/Raun/Resources/ResourceKey.cs`
- Create: `src/Raun/Resources/ResourceIdentity.cs`
- Create: `src/Raun/Resources/LifecycleVerb.cs`
- Test: `test/Raun.Test/Resources/ResourceKeyTests.cs`

- [ ] **Step 1: Write the failing test**

Create `test/Raun.Test/Resources/ResourceKeyTests.cs`:

```csharp
using Raun.Model;
using Xunit;

namespace Raun.Test.Resources;

/// <summary>
/// <see cref="ResourceKey"/> is a value-equality wrapper (string keys via implicit conversion, or any
/// value via <see cref="ResourceKey.FromValue"/>). <see cref="ResourceIdentity"/> pairs it with a type.
/// <see cref="LifecycleVerb"/>'s numeric order encodes lock/trace strength (Read weakest, Delete strongest).
/// </summary>
public class ResourceKeyTests
{
    [Fact]
    public void String_keys_compare_by_value()
    {
        ResourceKey a = "jane@acme.com";
        ResourceKey b = "jane@acme.com";

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, (ResourceKey)"bob@acme.com");
    }

    [Fact]
    public void FromValue_uses_the_value_s_own_equality()
    {
        var k1 = ResourceKey.FromValue(new IdOnly(7));
        var k2 = ResourceKey.FromValue(new IdOnly(7));

        Assert.Equal(k1, k2);                       // records compare by value
        Assert.Equal("7", ((ResourceKey)"7").ToString());
    }

    [Fact]
    public void Identity_pairs_type_and_key_and_reads_nicely()
    {
        var id = new ResourceIdentity(typeof(IdOnly), "k");

        Assert.Equal(new ResourceIdentity(typeof(IdOnly), "k"), id);
        Assert.Equal("IdOnly:k", id.ToString());
    }

    [Fact]
    public void Verb_strength_is_encoded_by_enum_order()
    {
        // Delete > Edit > Create > Load > Read — the dedup rule depends on this ordering.
        Assert.True(LifecycleVerb.Delete > LifecycleVerb.Edit);
        Assert.True(LifecycleVerb.Edit > LifecycleVerb.Create);
        Assert.True(LifecycleVerb.Create > LifecycleVerb.Load);
        Assert.True(LifecycleVerb.Load > LifecycleVerb.Read);
    }

    sealed record IdOnly(int Value);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~ResourceKeyTests"`
Expected: FAIL — `ResourceKey` / `ResourceIdentity` / `LifecycleVerb` do not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `src/Raun/Resources/ResourceKey.cs`:

```csharp
namespace Raun.Model;

/// <summary>
/// A symbolic resource key. Compares by the wrapped value's own equality, so a <c>string</c> key
/// (the common case, via the implicit conversion) compares by text and a whole-record key (the
/// value-equality fallback, via <see cref="FromValue"/>) compares by record equality.
/// </summary>
public readonly struct ResourceKey : IEquatable<ResourceKey>
{
    readonly object? _value;

    public ResourceKey(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
    }

    /// <summary>Wraps an arbitrary value as a key (used by the value-equality resolver fallback).</summary>
    public static ResourceKey FromValue(object value) => new(value);

    public static implicit operator ResourceKey(string value) => new(value);

    public bool Equals(ResourceKey other) => Equals(_value, other._value);

    public override bool Equals(object? obj) => obj is ResourceKey other && Equals(other);

    public override int GetHashCode() => _value?.GetHashCode() ?? 0;

    public override string ToString() => _value?.ToString() ?? "";
}
```

Create `src/Raun/Resources/ResourceIdentity.cs`:

```csharp
namespace Raun.Model;

/// <summary>A symbolic resource identity: the domain type plus its <see cref="ResourceKey"/>.</summary>
public readonly record struct ResourceIdentity(Type Type, ResourceKey Key)
{
    public override string ToString() => $"{Type.Name}:{Key}";
}
```

Create `src/Raun/Resources/LifecycleVerb.cs`:

```csharp
namespace Raun.Model;

/// <summary>
/// What a step does to a resource. The declaration order encodes <b>strength</b> (Read weakest …
/// Delete strongest); when one step touches the same identity more than once, the strongest verb is
/// the one recorded. In C2 these also map to lock modes (Read/Load shared, Create/Edit/Delete
/// exclusive), but C1 uses them only as trace labels.
/// </summary>
public enum LifecycleVerb
{
    Read,
    Load,
    Create,
    Edit,
    Delete,
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~ResourceKeyTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Build clean & commit**

Run: `dotnet build src/Raun/Raun.csproj` → expect 0 warnings / 0 errors.

```bash
jj describe -m "feat(resources): ResourceKey, ResourceIdentity, LifecycleVerb value types"
jj new
```

---

### Task 2: Identity interfaces (`IResource<TSelf>`, `IResourceIdentity`)

**Files:**
- Create: `src/Raun/Resources/IResource.cs`

These have no behavior of their own; they are exercised by the resolver in Task 3. This task only adds them and proves the build stays green (and that `latest` accepts the static-abstract member).

- [ ] **Step 1: Write the implementation**

Create `src/Raun/Resources/IResource.cs`:

```csharp
using Raun.Model;

namespace Raun;

/// <summary>
/// Marks a domain type as a resource whose identity key is computed at the <b>type</b> level (CRTP).
/// Hand-written form:
/// <code>
/// public sealed record User(string Email) : IResource&lt;User&gt;
/// {
///     public static ResourceKey KeyFor(User u) => u.Email;
/// }
/// </code>
/// The resolver invokes <see cref="KeyFor"/> for the first (highest-priority) link of its chain.
/// </summary>
/// <typeparam name="TSelf">The implementing type itself.</typeparam>
public interface IResource<TSelf>
    where TSelf : IResource<TSelf>
{
    /// <summary>Projects an instance to its identity key.</summary>
    static abstract ResourceKey KeyFor(TSelf self);
}

/// <summary>
/// Opt-in interface for resources whose key is computed at runtime from instance state, for types
/// that cannot expose a type-level <see cref="IResource{TSelf}"/> key.
/// </summary>
public interface IResourceIdentity
{
    /// <summary>Computes this instance's identity key.</summary>
    ResourceKey GetResourceKey();
}
```

- [ ] **Step 2: Build to verify it compiles clean**

Run: `dotnet build src/Raun/Raun.csproj`
Expected: 0 errors. **If CA1000 fires** on `KeyFor` ("Do not declare static members on generic types"), wrap just the `IResource<TSelf>` declaration:

```csharp
#pragma warning disable CA1000 // KeyFor is the type-level identity contract; a static abstract member is the intended shape.
public interface IResource<TSelf>
    where TSelf : IResource<TSelf>
{
    static abstract ResourceKey KeyFor(TSelf self);
}
#pragma warning restore CA1000
```

Do not add the pragma unless the build demands it (IDE0079 errors on unnecessary suppressions). Re-run the build until it is 0/0.

- [ ] **Step 3: Commit**

```bash
jj describe -m "feat(resources): IResource<TSelf> and IResourceIdentity identity interfaces"
jj new
```

---

### Task 3: `ResourceIdentityResolver` (4-link identity chain)

**Files:**
- Create: `src/Raun/Resources/ResourceIdentityResolver.cs`
- Test: `test/Raun.Test/Resources/ResourceIdentityResolverTests.cs`

The chain, first match wins (matching the design's order): **(1)** a type-level `KeyFor` (the `IResource<TSelf>` CRTP impl, found by reflection so the resolver needs no generic constraint), **(2)** a registered selector, **(3)** `IResourceIdentity.GetResourceKey()`, **(4)** whole-value equality.

- [ ] **Step 1: Write the failing test**

Create `test/Raun.Test/Resources/ResourceIdentityResolverTests.cs`:

```csharp
using Raun;
using Raun.Model;
using Xunit;

namespace Raun.Test.Resources;

/// <summary>
/// The resolver maps a value to a <see cref="ResourceIdentity"/> through a 4-link chain
/// (KeyFor → registered selector → IResourceIdentity → value-equality), first match wins.
/// </summary>
public class ResourceIdentityResolverTests
{
    sealed record User(string Email, bool Suspended = false) : IResource<User>
    {
        public static ResourceKey KeyFor(User u) => u.Email;
    }

    sealed record Ticket(int Number) : IResourceIdentity
    {
        public ResourceKey GetResourceKey() => "T-" + Number;
    }

    sealed record Plain(string Name);          // no KeyFor, no IResourceIdentity → value-equality

    [Fact]
    public void Link1_uses_type_level_KeyFor()
    {
        var r = new ResourceIdentityResolver();

        var id = r.IdentityOf(new User("jane@acme.com"));

        Assert.Equal(new ResourceIdentity(typeof(User), "jane@acme.com"), id);
    }

    [Fact]
    public void Edited_record_keeps_its_key()
    {
        var r = new ResourceIdentityResolver();
        var user = new User("jane@acme.com");

        Assert.Equal(r.IdentityOf(user), r.IdentityOf(user with { Suspended = true }));
    }

    [Fact]
    public void Link2_uses_a_registered_selector_for_types_without_KeyFor()
    {
        var r = new ResourceIdentityResolver();
        r.Identify<Plain>(p => p.Name);

        Assert.Equal(new ResourceIdentity(typeof(Plain), "widget"), r.IdentityOf(new Plain("widget")));
    }

    [Fact]
    public void Link3_uses_IResourceIdentity()
    {
        var r = new ResourceIdentityResolver();

        Assert.Equal(new ResourceIdentity(typeof(Ticket), "T-42"), r.IdentityOf(new Ticket(42)));
    }

    [Fact]
    public void Link4_falls_back_to_whole_value_equality()
    {
        var r = new ResourceIdentityResolver();

        var a = r.IdentityOf(new Plain("same"));
        var b = r.IdentityOf(new Plain("same"));
        Assert.Equal(a, b);                                 // equal records → equal identities
        Assert.NotEqual(a, r.IdentityOf(new Plain("other")));
    }

    [Fact]
    public void Earlier_links_win_over_later_ones()
    {
        var r = new ResourceIdentityResolver();
        r.Identify<User>(_ => "selector-should-not-win");   // User already has KeyFor (link 1)

        Assert.Equal(new ResourceIdentity(typeof(User), "jane@acme.com"),
            r.IdentityOf(new User("jane@acme.com")));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~ResourceIdentityResolverTests"`
Expected: FAIL — `ResourceIdentityResolver` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Raun/Resources/ResourceIdentityResolver.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using Raun.Model;

namespace Raun;

/// <summary>
/// Resolves a value's <see cref="ResourceIdentity"/> through a 4-link chain (first match wins):
/// <list type="number">
///   <item>a type-level <c>KeyFor</c> (the <see cref="IResource{TSelf}"/> CRTP implementation);</item>
///   <item>a selector registered via <see cref="Identify{T}(Func{T, ResourceKey})"/>;</item>
///   <item><see cref="IResourceIdentity.GetResourceKey"/>;</item>
///   <item>whole-value equality (for immutable identity-only records).</item>
/// </list>
/// A resolver instance is supplied per scenario run (via the service provider, see
/// <see cref="ScenarioContext"/>); selectors registered on it apply to that run.
/// </summary>
public sealed class ResourceIdentityResolver
{
    readonly ConcurrentDictionary<Type, Func<object, ResourceKey>?> _keyFor = new();
    readonly ConcurrentDictionary<Type, Func<object, ResourceKey>> _selectors = new();

    /// <summary>Registers a key projection for a type that cannot expose a type-level key (link 2).</summary>
    public void Identify<T>(Func<T, ResourceKey> keySelector)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _selectors[typeof(T)] = value => keySelector((T)value);
    }

    /// <summary>Resolves the identity of <paramref name="value"/>, keyed on its declared type <typeparamref name="T"/>.</summary>
    public ResourceIdentity IdentityOf<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new ResourceIdentity(typeof(T), KeyFor(typeof(T), value));
    }

    ResourceKey KeyFor(Type type, object value)
    {
        // Link 1: a public static KeyFor(type) — the IResource<TSelf> CRTP impl (hand-written today,
        // generator-emitted in C2). Found by reflection so the resolver needs no generic constraint.
        var keyFor = _keyFor.GetOrAdd(type, FindKeyFor);
        if (keyFor is not null)
        {
            return keyFor(value);
        }

        // Link 2: a registered selector.
        if (_selectors.TryGetValue(type, out var selector))
        {
            return selector(value);
        }

        // Link 3: a runtime-computed key.
        if (value is IResourceIdentity identity)
        {
            return identity.GetResourceKey();
        }

        // Link 4: the whole value, compared by its own equality.
        return ResourceKey.FromValue(value);
    }

    static Func<object, ResourceKey>? FindKeyFor(Type type)
    {
        var method = type.GetMethod(
            "KeyFor",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: [type],
            modifiers: null);

        if (method is null || method.ReturnType != typeof(ResourceKey))
        {
            return null;
        }

        return value => (ResourceKey)method.Invoke(null, [value])!;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~ResourceIdentityResolverTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Build clean & commit**

Run: `dotnet build src/Raun/Raun.csproj` → 0/0.

```bash
jj describe -m "feat(resources): ResourceIdentityResolver 4-link identity chain"
jj new
```

---

### Task 4: `ResourceEffect` trace model

**Files:**
- Create: `src/Raun/Resources/ResourceEffect.cs`
- Test: `test/Raun.Test/Resources/ResourceContextTests.cs` (created here, first test only)

`ResourceEffect` is a plain record; this task creates it and a single assertion via a placeholder test that will grow in Task 5. (Splitting the model out keeps Task 5 focused on behavior.)

- [ ] **Step 1: Write the failing test**

Create `test/Raun.Test/Resources/ResourceContextTests.cs`:

```csharp
using Raun.Model;
using Xunit;

namespace Raun.Test.Resources;

/// <summary>
/// A <see cref="ResourceEffect"/> is one entry in a step's resource trace. <see cref="ResourceContext"/>
/// (exercised in the rest of this file, added in Task 5) records them.
/// </summary>
public partial class ResourceContextTests
{
    [Fact]
    public void Effect_carries_verb_identity_data_and_owning_step()
    {
        var effect = new ResourceEffect
        {
            Verb = LifecycleVerb.Create,
            Identity = new ResourceIdentity(typeof(string), "k"),
            Data = "snapshot",
            StepId = "step-1",
            StepDisplayName = "user jane exists",
        };

        Assert.Equal(LifecycleVerb.Create, effect.Verb);
        Assert.Equal(new ResourceIdentity(typeof(string), "k"), effect.Identity);
        Assert.Equal("snapshot", effect.Data);
        Assert.Equal("step-1", effect.StepId);
        Assert.Equal("user jane exists", effect.StepDisplayName);
    }
}
```

> Note: this class is declared `partial` so Task 5 can add a second `partial` block in the same file without re-editing this one.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~ResourceContextTests"`
Expected: FAIL — `ResourceEffect` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Raun/Resources/ResourceEffect.cs`:

```csharp
namespace Raun.Model;

/// <summary>
/// One symbolic resource event recorded while a step ran: what it did (<see cref="Verb"/>), to which
/// resource (<see cref="Identity"/>), an optional human-readable snapshot of the value
/// (<see cref="Data"/>), and which step owned it. Effects flow out on <see cref="StepResult.Effects"/>
/// and feed tracing/reporting. No locking or real state is implied — these are symbolic.
/// </summary>
public sealed record ResourceEffect
{
    /// <summary>The lifecycle verb (also the trace label).</summary>
    public required LifecycleVerb Verb { get; init; }

    /// <summary>The resource this effect concerns.</summary>
    public required ResourceIdentity Identity { get; init; }

    /// <summary>An optional snapshot of the value (e.g. <c>value.ToString()</c>); null for key-only loads.</summary>
    public string? Data { get; init; }

    /// <summary>Stable id of the step that recorded this effect.</summary>
    public required string StepId { get; init; }

    /// <summary>Runtime-formatted display name of the recording step.</summary>
    public required string StepDisplayName { get; init; }

    /// <summary>When the effect was recorded.</summary>
    public DateTimeOffset Timestamp { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~ResourceContextTests"`
Expected: PASS (1 test).

- [ ] **Step 5: Build clean & commit**

Run: `dotnet build src/Raun/Raun.csproj` → 0/0.

```bash
jj describe -m "feat(resources): ResourceEffect trace model"
jj new
```

---

### Task 5: `ResourceContext` verbs + dedup, wired onto `ScenarioContext.Resources`

**Files:**
- Create: `src/Raun/Resources/ResourceContext.cs`
- Modify: `src/Raun/ScenarioContext.cs`
- Test: `test/Raun.Test/Resources/ResourceContextTests.cs` (add second partial block)

`ResourceContext` is constructed by `ScenarioContext` (internal ctor) and reached publicly via `ctx.Resources`. The resolver comes from `ctx.Services` when registered, else a fresh empty resolver. We test the whole thing through the public `ScenarioContext.Resources` path (no `InternalsVisibleTo` needed), injecting a configured resolver via a stub provider — mirroring the existing `ScenarioContextTests.StubProvider`.

- [ ] **Step 1: Write the failing tests**

Append to `test/Raun.Test/Resources/ResourceContextTests.cs`:

```csharp
namespace Raun.Test.Resources;

using Raun;
using Raun.Model;
using Xunit;

public partial class ResourceContextTests
{
    sealed record User(string Email, bool Suspended = false) : IResource<User>
    {
        public static ResourceKey KeyFor(User u) => u.Email;
    }

    // Builds a ScenarioContext whose Services resolves the given resolver, so ctx.Resources uses it.
    static ScenarioContext Context(ResourceIdentityResolver? resolver = null) =>
        new("step-1", "user jane exists",
            resolver is null ? null : new ResolverProvider(resolver),
            CancellationToken.None);

    [Fact]
    public void Verbs_record_effects_with_verb_identity_and_step()
    {
        var ctx = Context();

        ctx.Resources.Create(new User("jane@acme.com"));

        var effect = Assert.Single(ctx.Resources.Effects);
        Assert.Equal(LifecycleVerb.Create, effect.Verb);
        Assert.Equal(new ResourceIdentity(typeof(User), "jane@acme.com"), effect.Identity);
        Assert.Equal("step-1", effect.StepId);
        Assert.Equal("user jane exists", effect.StepDisplayName);
        Assert.Contains("jane@acme.com", effect.Data);    // ToString snapshot
    }

    [Fact]
    public async Task Each_verb_maps_to_its_lifecycle()
    {
        var ctx = Context();

        await ctx.Resources.Read(new User("r@x"));
        await ctx.Resources.Load(new User("l@x"));
        await ctx.Resources.Edit(new User("e@x"));
        await ctx.Resources.Delete(new User("d@x"));

        Assert.Equal(
            new[] { LifecycleVerb.Read, LifecycleVerb.Load, LifecycleVerb.Edit, LifecycleVerb.Delete },
            ctx.Resources.Effects.Select(e => e.Verb));
    }

    [Fact]
    public void Same_identity_dedups_to_the_strongest_verb()
    {
        var ctx = Context();
        var user = new User("jane@acme.com");

        ctx.Resources.Read(user);                          // weak
        ctx.Resources.Edit(user with { Suspended = true }); // stronger, same key

        var effect = Assert.Single(ctx.Resources.Effects);  // deduped to one
        Assert.Equal(LifecycleVerb.Edit, effect.Verb);
        Assert.Contains("Suspended = True", effect.Data);   // latest snapshot kept
    }

    [Fact]
    public void Distinct_identities_each_get_an_effect_in_order()
    {
        var ctx = Context();

        ctx.Resources.Read(new User("a@x"));
        ctx.Resources.Edit(new User("b@x"));

        Assert.Equal(new[] { "User:a@x", "User:b@x" },
            ctx.Resources.Effects.Select(e => e.Identity.ToString()));
    }

    [Fact]
    public void Key_based_Load_records_a_Load_with_no_snapshot()
    {
        var ctx = Context();

        ctx.Resources.Load<User>("admin@acme.com");

        var effect = Assert.Single(ctx.Resources.Effects);
        Assert.Equal(LifecycleVerb.Load, effect.Verb);
        Assert.Equal(new ResourceIdentity(typeof(User), "admin@acme.com"), effect.Identity);
        Assert.Null(effect.Data);
    }

    [Fact]
    public void Resolver_comes_from_services_when_registered()
    {
        var resolver = new ResourceIdentityResolver();
        resolver.Identify<Plain>(p => p.Tag);
        var ctx = Context(resolver);

        ctx.Resources.Read(new Plain("widget"));

        Assert.Equal(new ResourceIdentity(typeof(Plain), "widget"),
            Assert.Single(ctx.Resources.Effects).Identity);
    }

    sealed record Plain(string Tag);

    sealed class ResolverProvider(ResourceIdentityResolver resolver) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ResourceIdentityResolver) ? resolver : null;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~ResourceContextTests"`
Expected: FAIL — `ScenarioContext.Resources` and `ResourceContext` do not exist.

- [ ] **Step 3: Write `ResourceContext`**

Create `src/Raun/Resources/ResourceContext.cs`:

```csharp
using Raun.Model;

namespace Raun;

/// <summary>
/// The imperative resource surface a step reaches through <see cref="ScenarioContext.Resources"/>.
/// Each verb records a symbolic <see cref="ResourceEffect"/> for the owning step; within one step,
/// repeated touches of the same identity dedup to a single effect carrying the strongest verb. In C1
/// the verbs only record (no locking); they are <c>async</c> so the surface is unchanged when C2 makes
/// acquisition await a real lock. The generator lowers <c>[Creates]</c>/<c>[Reads]</c>/… to these calls.
/// </summary>
public sealed class ResourceContext
{
    readonly string _stepId;
    readonly string _stepDisplayName;
    readonly ResourceIdentityResolver _resolver;
    readonly object _gate = new();
    readonly List<ResourceEffect> _effects = [];
    readonly Dictionary<ResourceIdentity, int> _byIdentity = [];

    internal ResourceContext(string stepId, string stepDisplayName, ResourceIdentityResolver resolver)
    {
        _stepId = stepId;
        _stepDisplayName = stepDisplayName;
        _resolver = resolver;
    }

    /// <summary>Records the creation of a new resource (exclusive in C2).</summary>
    public ValueTask Create<T>(T value) => Record(LifecycleVerb.Create, _resolver.IdentityOf(value), Snapshot(value));

    /// <summary>Records loading/fetching an existing resource instance (shared in C2).</summary>
    public ValueTask Load<T>(T value) => Record(LifecycleVerb.Load, _resolver.IdentityOf(value), Snapshot(value));

    /// <summary>Records loading an existing resource by key, with no instance/snapshot (shared in C2).</summary>
    public ValueTask Load<T>(ResourceKey key) => Record(LifecycleVerb.Load, new ResourceIdentity(typeof(T), key), null);

    /// <summary>Records read-only use of an in-scope resource (shared in C2).</summary>
    public ValueTask Read<T>(T value) => Record(LifecycleVerb.Read, _resolver.IdentityOf(value), Snapshot(value));

    /// <summary>Records an edit to an existing resource (exclusive in C2).</summary>
    public ValueTask Edit<T>(T value) => Record(LifecycleVerb.Edit, _resolver.IdentityOf(value), Snapshot(value));

    /// <summary>Records deletion of a resource (exclusive in C2).</summary>
    public ValueTask Delete<T>(T value) => Record(LifecycleVerb.Delete, _resolver.IdentityOf(value), Snapshot(value));

    /// <summary>Effects recorded by this step, deduped by identity, in first-seen order.</summary>
    public IReadOnlyList<ResourceEffect> Effects
    {
        get { lock (_gate) { return _effects.ToArray(); } }
    }

    ValueTask Record(LifecycleVerb verb, ResourceIdentity identity, string? data)
    {
        lock (_gate)
        {
            if (_byIdentity.TryGetValue(identity, out var i))
            {
                var existing = _effects[i];
                _effects[i] = existing with
                {
                    Verb = verb > existing.Verb ? verb : existing.Verb,
                    Data = data ?? existing.Data,
                };
            }
            else
            {
                _byIdentity[identity] = _effects.Count;
                _effects.Add(new ResourceEffect
                {
                    Verb = verb,
                    Identity = identity,
                    Data = data,
                    StepId = _stepId,
                    StepDisplayName = _stepDisplayName,
                    Timestamp = DateTimeOffset.UtcNow,
                });
            }
        }

        return ValueTask.CompletedTask;
    }

    static string? Snapshot<T>(T value) => value?.ToString();
}
```

- [ ] **Step 4: Wire `ScenarioContext.Resources`**

In `src/Raun/ScenarioContext.cs`, add a backing field next to the existing ones (after line 14) and a lazy property (after the `Attachments` property, before the closing brace). The resolver is pulled from `Services` when registered, otherwise an empty one:

```csharp
    ResourceContext? _resources;

    /// <summary>
    /// The symbolic resource surface for this step (effects/tracing). Verbs record
    /// <see cref="Raun.Model.ResourceEffect"/>s that the scheduler copies onto the step result. The
    /// identity resolver is taken from <see cref="Services"/> when one is registered, else a fresh one.
    /// </summary>
    public ResourceContext Resources => _resources ??= new ResourceContext(
        StepId,
        StepDisplayName,
        Services?.GetService(typeof(ResourceIdentityResolver)) as ResourceIdentityResolver
            ?? new ResourceIdentityResolver());
```

(`ScenarioContext` is in namespace `Raun`, so `ResourceContext` and `ResourceIdentityResolver` need no qualification. `ResourceEffect` is in `Raun.Model`, referenced only in the doc comment.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~ResourceContextTests"`
Expected: PASS (7 tests total — 1 from Task 4 + 6 here).

- [ ] **Step 6: Build clean & commit**

Run: `dotnet build src/Raun/Raun.csproj` → 0/0.

```bash
jj describe -m "feat(resources): ResourceContext verbs with per-step dedup; ScenarioContext.Resources"
jj new
```

---

### Task 6: `StepResult.Effects` + scheduler capture

**Files:**
- Modify: `src/Raun/Model/StepResult.cs`
- Modify: `src/Raun/Scheduling/ScenarioScheduler.cs`
- Test: `test/Raun.Test/SchedulerTests.cs` (add one test)

The scheduler already copies `context.Logs` / `context.Attachments` into the `StepResult` for passed and failed steps (lines 232–233 and 258–259). Add `Effects` the same way. Skipped steps get the default empty list.

- [ ] **Step 1: Write the failing test**

Add to `test/Raun.Test/SchedulerTests.cs` (match the file's existing namespace/usings; it already references `Raun.Model`, `Raun.Scheduling`, and builds `ScenarioDefinition`/`ScenarioNode` inline — mirror the nearest existing test's node-construction style):

```csharp
    [Fact]
    public async Task Step_resource_effects_surface_on_the_result()
    {
        var node = new ScenarioNode
        {
            Index = 0,
            StepId = "s0",
            Phase = "Given",
            OperationName = "Create",
            DisplayNameTemplate = "create a thing",
            DependsOn = [],
            Invoke = async (_, ctx) =>
            {
                await ctx.Resources.Create("thing-1");   // string resource → value-equality identity
                return null;
            },
        };
        var definition = new ScenarioDefinition
        {
            ScenarioId = "sc",
            DisplayName = "effects",
            MethodName = "X.Effects",
            Nodes = [node],
        };

        var results = await new ScenarioScheduler().RunAsync(definition);

        var effect = Assert.Single(results[0].Effects);
        Assert.Equal(Raun.Model.LifecycleVerb.Create, effect.Verb);
        Assert.Equal("s0", effect.StepId);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~Step_resource_effects_surface_on_the_result"`
Expected: FAIL — `StepResult` has no `Effects` member.

- [ ] **Step 3: Add `Effects` to `StepResult`**

In `src/Raun/Model/StepResult.cs`, after the `Attachments` property (line 29), add:

```csharp

    /// <summary>Symbolic resource effects recorded during the step, in first-seen order.</summary>
    public IReadOnlyList<ResourceEffect> Effects { get; init; } = [];
```

- [ ] **Step 4: Capture effects in the scheduler**

In `src/Raun/Scheduling/ScenarioScheduler.cs`, in `RunNodeAsync`, add `Effects = context.Resources.Effects,` to **both** `StepResult` initializers:

- the passed result (after `Attachments = context.Attachments,` at line 233):

```csharp
                    Logs = context.Logs,
                    Attachments = context.Attachments,
                    Effects = context.Resources.Effects,
```

- the `Outcome(...)` local (after `Attachments = context.Attachments,` at line 259):

```csharp
                    Logs = context.Logs,
                    Attachments = context.Attachments,
                    Effects = context.Resources.Effects,
```

Leave the skip-path `StepResult` (in `ApplySkipAsync`) untouched — a skipped step never ran, so its `Effects` stays the default empty list.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~SchedulerTests"`
Expected: PASS (existing scheduler tests + the new one).

- [ ] **Step 6: Build clean & commit**

Run: `dotnet build` → 0/0.

```bash
jj describe -m "feat(resources): StepResult.Effects captured by the scheduler"
jj new
```

---

### Task 7: Role attributes (`[Creates]`, `[Loads]`, `[Reads]`, `[Edits]`, `[Deletes]`)

**Files:**
- Create: `src/Raun/Resources/ResourceRoleAttributes.cs`
- Test: `test/Raun.Test/Resources/ResourceRoleAttributeTests.cs`

Pure declarations consumed by the generator/analyzer. The `AttributeUsage` targets match the design's role menu (return roles: Creates/Loads/Edits; parameter roles: Reads/Edits/Deletes). `[Creates]`/`[Loads]`/`[Edits]` also allow `Method` as the single-resource "targets the return" shorthand.

- [ ] **Step 1: Write the failing test**

Create `test/Raun.Test/Resources/ResourceRoleAttributeTests.cs`:

```csharp
using System;
using System.Linq;
using Raun;
using Xunit;

namespace Raun.Test.Resources;

/// <summary>The role attributes exist with the right usage targets (return/parameter/method shorthand).</summary>
public class ResourceRoleAttributeTests
{
    static AttributeTargets Targets<T>() where T : Attribute =>
        typeof(T).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>().Single().ValidOn;

    [Fact]
    public void Return_roles_allow_return_and_method()
    {
        Assert.True(Targets<CreatesAttribute>().HasFlag(AttributeTargets.ReturnValue));
        Assert.True(Targets<CreatesAttribute>().HasFlag(AttributeTargets.Method));
        Assert.True(Targets<LoadsAttribute>().HasFlag(AttributeTargets.ReturnValue));
    }

    [Fact]
    public void Parameter_roles_allow_parameters()
    {
        Assert.True(Targets<ReadsAttribute>().HasFlag(AttributeTargets.Parameter));
        Assert.True(Targets<DeletesAttribute>().HasFlag(AttributeTargets.Parameter));
    }

    [Fact]
    public void Edits_is_valid_on_both_parameter_and_return()
    {
        var t = Targets<EditsAttribute>();
        Assert.True(t.HasFlag(AttributeTargets.Parameter));
        Assert.True(t.HasFlag(AttributeTargets.ReturnValue));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~ResourceRoleAttributeTests"`
Expected: FAIL — the attributes do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Raun/Resources/ResourceRoleAttributes.cs`:

```csharp
namespace Raun;

/// <summary>Return/method role: the step produces a <b>new</b> resource (exclusive in C2).</summary>
[AttributeUsage(AttributeTargets.ReturnValue | AttributeTargets.Method)]
public sealed class CreatesAttribute : Attribute;

/// <summary>Return/method role: the step returns an <b>existing</b> resource it loaded (shared in C2).</summary>
[AttributeUsage(AttributeTargets.ReturnValue | AttributeTargets.Method)]
public sealed class LoadsAttribute : Attribute;

/// <summary>Parameter role: the step only reads the resource (shared in C2).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ReadsAttribute : Attribute;

/// <summary>Parameter or return/method role: the step mutates the resource (exclusive in C2).</summary>
[AttributeUsage(AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.Method)]
public sealed class EditsAttribute : Attribute;

/// <summary>Parameter role: the step removes the resource (exclusive in C2).</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class DeletesAttribute : Attribute;
```

> If CA1813 ("avoid unsealed attributes") or CA1019 fires, note these are already `sealed` and parameterless — no action expected. Build to confirm.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/Raun.Test/Raun.Test.csproj --filter "FullyQualifiedName~ResourceRoleAttributeTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Build clean & commit**

Run: `dotnet build src/Raun/Raun.csproj` → 0/0.

```bash
jj describe -m "feat(resources): role attributes Creates/Loads/Reads/Edits/Deletes"
jj new
```

---

### Task 8: Generator lowering — read roles, build claims, inject `ctx.Resources.*` calls

**Files:**
- Modify: `src/Raun.Generator/Lowering/Ir.cs`
- Modify: `src/Raun.Generator/Lowering/AttributeReader.cs`
- Modify: `src/Raun.Generator/Lowering/ScenarioParser.cs`
- Modify: `src/Raun.Generator/Emit/ScenarioEmitter.cs`
- Modify: `test/Raun.Generator.Test/SampleSources.cs` (add a resource DSL + scenario — **do not touch the existing constants**, so the existing snapshots stay byte-identical)
- Test: `test/Raun.Generator.Test/ResourceLoweringTests.cs`

This is the one cohesive "make the lowering work" task. The single behavioral gate compiles a resource scenario and runs it through the real scheduler, asserting the effects appear on the `StepResult`s. The implementation spans four files; build it test-first.

**Target generated shape** — all resource calls are injected **after** the step-call statement (the line-mapped call statement stays first, so PDB sequence points and the no-claims output are untouched):

```csharp
// Given.UserExists  →  [return: Creates]
Invoke = static async (__inputs, __ctx) =>
{
    var __r = await Given.UserExists("jane@acme.com");
    await __ctx.Resources.Create(__r);
    return (object?)__r;
}

// When.Suspend([Edits] User user)  →  [return: Edits]
Invoke = static async (__inputs, __ctx) =>
{
    var __r = await When.Suspend(__inputs.Get<global::Demo.User>(0));
    await __ctx.Resources.Edit(__inputs.Get<global::Demo.User>(0));   // param role
    await __ctx.Resources.Edit(__r);                                  // return role
    return (object?)__r;
}

// Then.CannotSignIn([Reads] User user)  →  void
Invoke = static async (__inputs, __ctx) =>
{
    await Then.CannotSignIn(__inputs.Get<global::Demo.User>(1));
    await __ctx.Resources.Read(__inputs.Get<global::Demo.User>(1));
    return (object?)null;
}
```

At runtime, `Suspend`'s two `Edit`s dedup (same key) to one effect. A step with **no** roles emits **byte-identical** code to today (the claim list is empty, so nothing is inserted) — preserving existing snapshots. Effects are recorded only if the step call itself succeeds (a throwing step records none), which is the intended "what the step did" semantic.

- [ ] **Step 1: Write the failing test + sample sources**

Add to `test/Raun.Generator.Test/SampleSources.cs` (new constants only):

```csharp
    // A resource-aware DSL: User is a CRTP resource; roles drive the effect stream.
    public const string ResourceDsl =
        """
        using System.Threading.Tasks;
        using Raun;
        using Raun.Model;

        namespace Demo;

        public sealed record User(string Email, bool Suspended = false) : IResource<User>
        {
            public static ResourceKey KeyFor(User u) => u.Email;
        }

        public sealed record Slot(int Id) : IResource<Slot>
        {
            public static ResourceKey KeyFor(Slot s) => s.Id.ToString();
        }

        public sealed record Appointment(User User, Slot Slot) : IResource<Appointment>
        {
            public static ResourceKey KeyFor(Appointment a) => a.User.Email + "@" + a.Slot.Id;
        }

        public static class ResourceDslImpl
        {
            extension(Given)
            {
                [StepName("user {email} exists")]
                [return: Creates]
                public static async Task<User> UserExists(string email)
                {
                    await Task.Yield();
                    return new User(email);
                }
            }

            extension(When)
            {
                [StepName("{user} is suspended")]
                [return: Edits]
                public static async Task<User> Suspend([Edits] User user)
                {
                    await Task.Yield();
                    return user with { Suspended = true };
                }

                [StepName("{user} books {slot}")]
                [return: Creates]
                public static async Task<Appointment> Book([Reads] User user, [Edits] Slot slot)
                {
                    await Task.Yield();
                    return new Appointment(user, slot);
                }
            }

            extension(Then)
            {
                [StepName("{user} cannot sign in")]
                public static Task CannotSignIn([Reads] User user) => Task.CompletedTask;
            }
        }
        """;

    public const string ResourceScenario =
        """

        public static class ResourceScenarios
        {
            [Scenario("a suspended user cannot sign in")]
            public static async Task SuspendedUserCannotSignIn()
            {
                var user = await Given.UserExists("jane@acme.com");
                user = await When.Suspend(user);
                await Then.CannotSignIn(user);
            }
        }
        """;
```

Create `test/Raun.Generator.Test/ResourceLoweringTests.cs`:

```csharp
using System.Linq;
using Raun.Model;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>
/// Verifies the generator lowers resource role attributes (<c>[Creates]</c>/<c>[Reads]</c>/<c>[Edits]</c>
/// on parameters and returns) into <c>ctx.Resources.*</c> calls that produce the effect stream when the
/// generated scenario runs through the real scheduler.
/// </summary>
public class ResourceLoweringTests
{
    static async Task<IReadOnlyList<StepResult>> RunResourceScenario()
    {
        var result = GeneratorHarness.Run(SampleSources.ResourceDsl + SampleSources.ResourceScenario);
        result.AssertCompiles();
        return await result.Definitions().Single().RunAsync();
    }

    [Fact]
    public async Task Return_creates_records_a_create_effect()
    {
        var results = await RunResourceScenario();

        var create = Assert.Single(results[0].Effects);     // Given.UserExists
        Assert.Equal(LifecycleVerb.Create, create.Verb);
        Assert.Equal("User:jane@acme.com", create.Identity.ToString());
    }

    [Fact]
    public async Task Param_and_return_edits_dedup_to_one_edit()
    {
        var results = await RunResourceScenario();

        var edit = Assert.Single(results[1].Effects);        // When.Suspend: [Edits] param + [return: Edits]
        Assert.Equal(LifecycleVerb.Edit, edit.Verb);
        Assert.Equal("User:jane@acme.com", edit.Identity.ToString());
    }

    [Fact]
    public async Task Read_param_records_a_read_effect()
    {
        var results = await RunResourceScenario();

        var read = Assert.Single(results[2].Effects);        // Then.CannotSignIn([Reads] user)
        Assert.Equal(LifecycleVerb.Read, read.Verb);
        Assert.Equal("User:jane@acme.com", read.Identity.ToString());
    }

    [Fact]
    public void Role_free_scenarios_inject_no_resource_calls()
    {
        // Existing DSL has no roles: generated code must contain no Resources call (snapshot stability).
        var result = GeneratorHarness.Run(SampleSources.Dsl + SampleSources.LinearScenario);
        result.AssertCompiles();

        Assert.DoesNotContain(".Resources.", result.GeneratedSource);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj --filter "FullyQualifiedName~ResourceLoweringTests"`
Expected: FAIL — the three effect tests fail (no effects recorded; `Assert.Single` finds 0). The `Role_free` test should already pass.

- [ ] **Step 3: Add `ResourceClaim` to the IR**

In `src/Raun.Generator/Lowering/Ir.cs`, add the record (near `ParsedStep`) and a field on `ParsedStep`:

```csharp
/// <summary>
/// A lowered resource role on a step: which verb to record (<c>Create</c>/<c>Load</c>/<c>Read</c>/
/// <c>Edit</c>/<c>Delete</c>), the C# expression that yields the resource value (a rewritten argument
/// for parameter roles, or <c>__r</c> for return roles), and whether it fires after the step call.
/// </summary>
internal sealed record ResourceClaim(string Verb, string Expression, bool IsReturn);
```

Add to `ParsedStep` (alongside the other `IReadOnlyList<...>` members):

```csharp
    public IReadOnlyList<ResourceClaim> ResourceClaims { get; init; } = [];
```

- [ ] **Step 4: Add role readers to `AttributeReader`**

In `src/Raun.Generator/Lowering/AttributeReader.cs`, add (mirroring the existing name-based attribute matching):

```csharp
    /// <summary>The resource verb for a parameter's role attribute, or null if it has none.</summary>
    public static string? ParameterRole(IParameterSymbol parameter)
        => RoleVerb(parameter.GetAttributes(), parameterRoles: true);

    /// <summary>
    /// The resource verb for a method's return role — return-type attributes first, then method-level
    /// shorthand (<c>[Creates]</c>/<c>[Loads]</c>/<c>[Edits]</c> target the return) — or null.
    /// </summary>
    public static string? ReturnRole(IMethodSymbol method)
        => RoleVerb(method.GetReturnTypeAttributes(), parameterRoles: false)
            ?? RoleVerb(method.GetAttributes(), parameterRoles: false);

    static string? RoleVerb(System.Collections.Immutable.ImmutableArray<AttributeData> attributes, bool parameterRoles)
    {
        foreach (var attr in attributes)
        {
            switch (attr.AttributeClass?.Name)
            {
                case "ReadsAttribute" when parameterRoles: return "Read";
                case "DeletesAttribute" when parameterRoles: return "Delete";
                case "EditsAttribute": return "Edit";
                case "CreatesAttribute" when !parameterRoles: return "Create";
                case "LoadsAttribute" when !parameterRoles: return "Load";
            }
        }

        return null;
    }
```

(`[Edits]` is valid on both parameter and return, so it matches in either mode.)

- [ ] **Step 5: Build claims in `ScenarioParser.BuildStep`**

In `src/Raun.Generator/Lowering/ScenarioParser.cs`, inside `BuildStep`, after `var replacements = BuildReplacements();` (line 352), build the claim list and a per-argument rewriter, then set `ResourceClaims` on the step. Add this helper method and call it:

```csharp
    // Add a call to this just before constructing the ParsedStep:
    //   var resourceClaims = BuildResourceClaims(invocation, method, replacements);
    // and set `ResourceClaims = resourceClaims,` in the initializer.

    static List<ResourceClaim> BuildResourceClaims(
        InvocationExpressionSyntax invocation,
        IMethodSymbol method,
        Dictionary<string, string> replacements)
    {
        var claims = new List<ResourceClaim>();
        var rewriter = new IdentifierReplacer(replacements);
        var args = invocation.ArgumentList.Arguments;

        // Parameter roles: skip a trailing ScenarioContext parameter (it carries no resource role).
        var paramCount = SymbolHelpers.WantsContext(method, args.Count)
            ? method.Parameters.Length - 1
            : method.Parameters.Length;

        for (var i = 0; i < paramCount; i++)
        {
            var role = AttributeReader.ParameterRole(method.Parameters[i]);
            if (role is null)
            {
                continue;
            }

            var argExpr = FindArgumentExpression(args, i, method.Parameters[i].Name);
            if (argExpr is null)
            {
                continue;   // optional parameter not supplied — nothing to record
            }

            var text = rewriter.Visit(argExpr).ToFullString().Trim();
            claims.Add(new ResourceClaim(role, text, IsReturn: false));
        }

        // Return role: only meaningful when the step yields a value (__r exists).
        var returnRole = AttributeReader.ReturnRole(method);
        if (returnRole is not null && SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var rt) && rt is not null)
        {
            claims.Add(new ResourceClaim(returnRole, "__r", IsReturn: true));
        }

        return claims;
    }

    // Maps a parameter (by name for named args, else by position) to its argument value expression.
    static ExpressionSyntax? FindArgumentExpression(
        SeparatedSyntaxList<ArgumentSyntax> args,
        int index,
        string parameterName)
    {
        foreach (var arg in args)
        {
            if (arg.NameColon?.Name.Identifier.Text == parameterName)
            {
                return arg.Expression;
            }
        }

        if (index < args.Count && args[index].NameColon is null)
        {
            return args[index].Expression;
        }

        return null;
    }
```

Then in the `new ParsedStep { … }` initializer, add `ResourceClaims = BuildResourceClaims(invocation, method, replacements),`.

> Note: claims use `invocation` (the original, pre-rewrite argument syntax), exactly as `CollectDataflowDeps` and `BuildCallText` already do — so the LINQ-unrolled path (which passes a substituted `invocation` plus a separate `semanticNode`) still rewrites the right argument text.

- [ ] **Step 6: Inject the calls in `ScenarioEmitter.BuildInvokeLambda`**

In `src/Raun.Generator/Emit/ScenarioEmitter.cs`, change `BuildInvokeLambda` to insert the resource-call statements (all of them, parameter and return roles alike) **between** the step-call statement and the `return`. The call statement stays first and keeps its `LineMappedTrivia`; the inserted statements are hidden. When the claim list is empty the body is byte-identical to today. Replace `BuildInvokeLambda` and add `ResourceCallStatement`:

```csharp
    static ParenthesizedLambdaExpressionSyntax BuildInvokeLambda(ParsedStep step)
    {
        var callExpr = ParseExpression(step.InvokeCallText);
        var awaitExpr = AwaitExpression(callExpr);
        var resourceCalls = step.ResourceClaims.Select(ResourceCallStatement).ToList();

        List<StatementSyntax> bodyStatements;
        if (step.HasResult)
        {
            // var __r = await CALL;  (return-role claims reference __r, so they sit after this)
            var varDecl = LocalDeclarationStatement(
                VariableDeclaration(IdentifierName("var"))
                    .WithVariables(SingletonSeparatedList(
                        VariableDeclarator(Identifier("__r"))
                            .WithInitializer(EqualsValueClause(awaitExpr)))))
                .WithLeadingTrivia(LineMappedTrivia(step));
            var returnStmt = ReturnStatement(
                CastExpression(
                    NullableType(PredefinedType(Token(SyntaxKind.ObjectKeyword))),
                    IdentifierName("__r")))
                .WithLeadingTrivia(HiddenTrivia());
            bodyStatements = [varDecl, .. resourceCalls, returnStmt];
        }
        else
        {
            // await CALL;  (void steps carry only parameter-role claims — there is no __r)
            var awaitStmt = ExpressionStatement(awaitExpr).WithLeadingTrivia(LineMappedTrivia(step));
            var returnStmt = ReturnStatement(
                CastExpression(
                    NullableType(PredefinedType(Token(SyntaxKind.ObjectKeyword))),
                    LiteralExpression(SyntaxKind.NullLiteralExpression)))
                .WithLeadingTrivia(HiddenTrivia());
            bodyStatements = [awaitStmt, .. resourceCalls, returnStmt];
        }

        return ParenthesizedLambdaExpression()
            .WithModifiers(TokenList(Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.AsyncKeyword)))
            .WithParameterList(ParameterList(SeparatedList(new[]
            {
                Parameter(Identifier("__inputs")),
                Parameter(Identifier("__ctx")),
            })))
            .WithBlock(Block(bodyStatements));
    }

    /// <summary>Builds <c>await __ctx.Resources.&lt;Verb&gt;(&lt;expr&gt;);</c> with hidden trivia (synthetic).</summary>
    static StatementSyntax ResourceCallStatement(ResourceClaim claim)
        => ExpressionStatement(
                AwaitExpression(
                    ParseExpression($"__ctx.Resources.{claim.Verb}({claim.Expression})")))
            .WithLeadingTrivia(HiddenTrivia());
```

Because the line-mapped call statement is still the **first** body statement (exactly as today) and the inserted statements are hidden, `LineMappingPdbTests`/`HarnessPdbTests` stay valid and the empty-claim path reproduces today's output verbatim. The collection-expression spread (`[varDecl, .. resourceCalls, returnStmt]`) is available under `LangVersion=latest`.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj`
Expected: PASS — `ResourceLoweringTests` (4) green, and the **full** generator suite green, including `LineMappingPdbTests`, `HarnessPdbTests`, and the four `GeneratorSnapshotTests` (unchanged because the existing sample DSL has no roles). If a snapshot test fails, inspect the `.received.cs` diff: it must be empty for the role-free scenarios — if it isn't, the role-free output path regressed; fix `BuildInvokeLambda` so an empty claim list reproduces the original statements exactly.

- [ ] **Step 8: Build clean & commit**

Run: `dotnet build` → 0/0.

```bash
jj describe -m "feat(generator): lower resource roles to ctx.Resources.* effect calls"
jj new
```

---

### Task 9: RAUN009 — resource access must be declared

**Files:**
- Modify: `src/Raun.Generator/Analysis/Descriptors.cs`
- Modify: `src/Raun.Generator/Analysis/ScenarioAnalyzer.cs`
- Modify: `src/Raun.Generator/AnalyzerReleases.Unshipped.md`
- Test: `test/Raun.Generator.Test/AnalyzerTests.cs` (add tests)

RAUN009 fires on a **`[StepName]` method** whose resource-typed parameter or return value carries no role. "Resource-typed" = implements `IResource<>` or `IResourceIdentity` (the C1 markers). The check lives in the existing `[StepName]` analysis branch.

- [ ] **Step 1: Write the failing tests**

Add to `test/Raun.Generator.Test/AnalyzerTests.cs`:

```csharp
    [Fact]
    public void RAUN009_is_a_supported_diagnostic()
    {
        var analyzer = new Raun.Generator.Analysis.ScenarioAnalyzer();
        Assert.Contains(analyzer.SupportedDiagnostics, d => d.Id == "RAUN009");
    }

    [Fact]
    public async Task RAUN009_unannotated_resource_parameter()
    {
        var source =
            """
            using System.Threading.Tasks;
            using Raun;
            using Raun.Model;
            namespace Bad;
            public sealed record User(string Email) : IResource<User>
            {
                public static ResourceKey KeyFor(User u) => u.Email;
            }
            public static class BadDsl
            {
                extension(When)
                {
                    [StepName("suspend a user")]
                    public static Task Suspend(User user) => Task.CompletedTask;  // no role!
                }
            }
            """;

        AssertHas(await GeneratorHarness.AnalyzeAsync(source), "RAUN009");
    }

    [Fact]
    public async Task RAUN009_unannotated_resource_return()
    {
        var source =
            """
            using System.Threading.Tasks;
            using Raun;
            using Raun.Model;
            namespace Bad;
            public sealed record User(string Email) : IResource<User>
            {
                public static ResourceKey KeyFor(User u) => u.Email;
            }
            public static class BadDsl
            {
                extension(Given)
                {
                    [StepName("a user")]
                    public static Task<User> AUser() => Task.FromResult(new User("x"));  // no return role!
                }
            }
            """;

        AssertHas(await GeneratorHarness.AnalyzeAsync(source), "RAUN009");
    }

    [Fact]
    public async Task RAUN009_clean_when_roles_present()
    {
        // The resource DSL declares roles on every resource param/return — no RAUN009.
        var diagnostics = await GeneratorHarness.AnalyzeAsync(
            SampleSources.ResourceDsl + SampleSources.ResourceScenario);

        Assert.DoesNotContain(diagnostics, d => d.Id == "RAUN009");
    }

    [Fact]
    public async Task RAUN009_does_not_fire_on_non_resource_types()
    {
        // The role-free appointment DSL uses plain records (no IResource) — no RAUN009.
        Assert.DoesNotContain(await Analyze(SampleSources.LinearScenario), d => d.Id == "RAUN009");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj --filter "FullyQualifiedName~RAUN009"`
Expected: FAIL — RAUN009 is not defined / not raised.

- [ ] **Step 3: Add the descriptor**

In `src/Raun.Generator/Analysis/Descriptors.cs`, after `UnboundPlaceholder` (line 80):

```csharp

    public static readonly DiagnosticDescriptor MissingResourceRole = new(
        "RAUN009",
        "Resource access must be declared",
        "Resource-typed {0} '{1}' must declare its access: [Reads], [Edits], or [Deletes] on a parameter, "
            + "or [Creates], [Loads], or [Edits] on the return — there is no default",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
```

- [ ] **Step 4: Register and emit it in `ScenarioAnalyzer`**

In `src/Raun.Generator/Analysis/ScenarioAnalyzer.cs`:

(a) add to `SupportedDiagnostics` (after `Descriptors.UnboundPlaceholder,`):

```csharp
        Descriptors.MissingResourceRole,
```

(b) call a new check from the `[StepName]` branch in `AnalyzeMethodCore` (after the existing `AnalyzeStepName(context, symbol);` at line 65):

```csharp
            AnalyzeStepResources(context, symbol);
```

(c) add the check + a resource-type predicate:

```csharp
    static void AnalyzeStepResources(SyntaxNodeAnalysisContext context, IMethodSymbol method)
    {
        foreach (var parameter in method.Parameters)
        {
            if (IsResourceType(parameter.Type) && AttributeReader.ParameterRole(parameter) is null)
            {
                var location = parameter.Locations.FirstOrDefault() ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.MissingResourceRole, location, "parameter", parameter.Name));
            }
        }

        if (SymbolHelpers.TryUnwrapReturn(method.ReturnType, out var resultType)
            && resultType is not null
            && IsResourceType(resultType)
            && AttributeReader.ReturnRole(method) is null)
        {
            var location = method.Locations.FirstOrDefault() ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.MissingResourceRole, location, "return", method.Name));
        }
    }

    /// <summary>A type is a C1 resource if it implements Raun's IResource&lt;T&gt; or IResourceIdentity.</summary>
    static bool IsResourceType(ITypeSymbol type)
        => type.AllInterfaces.Any(i =>
            i.ContainingNamespace?.ToDisplayString(SymbolHelpers.NoGlobal) == "Raun"
            && ((i.Name == "IResource" && i.Arity == 1) || i.Name == "IResourceIdentity"));
```

> A trailing `ScenarioContext` parameter is `Raun.ScenarioContext`, which is not a resource type, so it is naturally skipped — no special-casing needed.

- [ ] **Step 5: Record the new rule**

In `src/Raun.Generator/AnalyzerReleases.Unshipped.md`, add under `### New Rules`:

```
RAUN009 | Raun.Usage | Error | Resource access must be declared
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj --filter "FullyQualifiedName~AnalyzerTests"`
Expected: PASS — all RAUN009 tests plus the existing analyzer tests (verify `Valid_scenarios_produce_no_diagnostics` still passes — role-free plain records must not trip RAUN009).

- [ ] **Step 7: Build clean & commit**

Run: `dotnet build` → 0/0 (the `AnalyzerReleases` tracking analyzer (RS2008) errors if a shipped/unshipped row is missing — confirm it is satisfied).

```bash
jj describe -m "feat(generator): RAUN009 — resource access must be declared"
jj new
```

---

### Task 10: Snapshot the resource lowering (review artifact)

**Files:**
- Modify: `test/Raun.Generator.Test/GeneratorSnapshotTests.cs`
- Create: `test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.Resource_scenario#RaunScenarios.g.verified.cs` (via accept)

A snapshot of the resource scenario's generated code is the human-reviewable record of the injected `ctx.Resources.*` calls (memory `working-style`: behavioral tests primary, Verify snapshots a secondary review artifact).

- [ ] **Step 1: Add the snapshot test**

Append to `test/Raun.Generator.Test/GeneratorSnapshotTests.cs` (mirror the existing `Linear_scenario` test):

```csharp
    [Fact]
    public Task Resource_scenario() =>
        Verify(GeneratorHarness.RunDriver(SampleSources.ResourceDsl + SampleSources.ResourceScenario))
            .UseDirectory("Snapshots");
```

- [ ] **Step 2: Run to produce the received snapshot**

Run: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj --filter "FullyQualifiedName~Resource_scenario"`
Expected: FAIL — Verify reports a new/pending snapshot and writes
`Snapshots/GeneratorSnapshotTests.Resource_scenario#RaunScenarios.g.received.cs`.

- [ ] **Step 3: Review and accept the snapshot**

Open the `.received.cs` and confirm the generated invoke lambdas match the **Target generated shape** from Task 8: each step's `await CALL` statement comes first, then the `await __ctx.Resources.<Verb>(…)` lines — `Given.UserExists` adds `await __ctx.Resources.Create(__r);`; `When.Suspend` adds `await __ctx.Resources.Edit(__inputs.Get<global::Demo.User>(0));` then `await __ctx.Resources.Edit(__r);`; `Then.CannotSignIn` adds `await __ctx.Resources.Read(…);`. When correct, accept it (rename received → verified):

```bash
mv "test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.Resource_scenario#RaunScenarios.g.received.cs" \
   "test/Raun.Generator.Test/Snapshots/GeneratorSnapshotTests.Resource_scenario#RaunScenarios.g.verified.cs"
```

(PowerShell: `Move-Item -Force <received> <verified>`.)

- [ ] **Step 4: Re-run to confirm green**

Run: `dotnet test test/Raun.Generator.Test/Raun.Generator.Test.csproj --filter "FullyQualifiedName~GeneratorSnapshotTests"`
Expected: PASS (all snapshots, including the new one).

- [ ] **Step 5: Commit**

```bash
jj describe -m "test(generator): snapshot of resource-role lowering"
jj new
```

---

### Task 11: End-to-end — sample DSL roles + reporter surfacing

**Files:**
- Modify: `samples/AppointmentTests/AppointmentDsl.cs`
- Modify: `samples/AppointmentTests/Scenarios.cs` (only if needed to exercise an edited resource; inspect first)
- Modify: `src/Raun.Mtp/RaunStepReporter.cs`
- Test: `test/Raun.Mtp.Test/RaunStepReporterTests.cs` (add a test)

Make the sample exercise the effect stream, and surface effects through the observer (the C1 "stream through the observer" deliverable; the HTML report lifeline is feature B and out of scope). Surface effects as standard output, mirroring `AddOutput` for logs.

- [ ] **Step 1 (sample): make the appointment domain types resources and annotate roles**

Read `samples/AppointmentTests/Scenarios.cs` first to see which steps consume which values, then in `samples/AppointmentTests/AppointmentDsl.cs`:

- make `Patient`, `Slot`, `Appointment`, `User` CRTP resources, e.g.:

```csharp
public sealed record Patient(string Name) : IResource<Patient>
{
    public static ResourceKey KeyFor(Patient p) => p.Name;
}

public sealed record Slot(int Id) : IResource<Slot>
{
    public static ResourceKey KeyFor(Slot s) => s.Id.ToString();
}

public sealed record Appointment(Patient Patient, Slot Slot) : IResource<Appointment>
{
    public static ResourceKey KeyFor(Appointment a) => a.Patient.Name + "@" + a.Slot.Id;
}

public sealed record User(string Name) : IResource<User>
{
    public static ResourceKey KeyFor(User u) => u.Name;
}
```

(add `using Raun.Model;` for `ResourceKey`.) Keep `ImportResult` a plain record (not a resource) so RAUN009 doesn't require a role there.

- annotate every resource-typed parameter/return (RAUN009 now enforces this or the sample won't build):

```csharp
[StepName("Given patient {name} exists")]
[return: Creates]
public static async Task<Patient> PatientExists(string name) { await Task.Yield(); return new Patient(name); }

[StepName("Given an available slot exists")]
[return: Creates]
public static async Task<Slot> AvailableSlot() { await Task.Yield(); return new Slot(1); }

[StepName("Given user {name} exists")]
[return: Creates]
public static async Task<User> UserExists(string name) { await Task.Yield(); return new User(name); }

[StepName("When creating an appointment")]
[return: Creates]
public static async Task<Appointment> CreateAppointment([Reads] Patient patient, [Edits] Slot slot)
{ await Task.Yield(); return new Appointment(patient, slot); }

[StepName("When importing the users")]
public static async Task<ImportResult> ImportUsers([Reads] User[] users)   // see note below
{ await Task.Yield(); return new ImportResult(users.Length); }

[StepName("Then the appointment should exist")]
public static Task AppointmentExists([Reads] Appointment appointment) { /* asserts */ return Task.CompletedTask; }
```

> **`User[]` array parameter:** the analyzer's `IsResourceType` checks the parameter type itself — an *array* `User[]` does not implement `IResource<>`, so RAUN009 does not require a role on it, and the generator's per-parameter claim builder will not emit a call for it (the element type is the resource, not the array). Annotating `[Reads]` on `User[]` is harmless but records nothing in C1 (the resolver is handed the array, whose identity falls to value-equality). Leave the array case unannotated for C1; element-wise array effects are a C2 refinement. If `DatabaseIsClean` / `ImportShouldContainUsers` take non-resource params, they need no roles.

- [ ] **Step 2 (sample): build the sample to verify roles satisfy RAUN009 and lowering compiles**

Run: `dotnet build samples/AppointmentTests/AppointmentTests.csproj`
Expected: 0 errors. If RAUN009 fires, a resource-typed param/return is missing its role — add it. If a role is on a non-resource, no harm.

- [ ] **Step 3 (sample): run the sample and confirm steps still pass**

Run: `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --filter-method "*"`
(Per memory `raun-mtp-redesign`, the sample is an MTP app; use the MTP `-- --filter-*` syntax, not VSTest filters.)
Expected: all scenario step nodes pass (resource lowering must not change behavior — effects are recorded silently).

- [ ] **Step 4 (reporter): write the failing test**

Add to `test/Raun.Mtp.Test/RaunStepReporterTests.cs` — a near-copy of the existing `Logs_surface_as_standard_output_on_the_finished_update` test (its helpers `Definition`/`Node`/`NewReporter` and `using Raun.Model;` are already in the file):

```csharp
    [Fact]
    public async Task Resource_effects_surface_as_standard_output_on_the_finished_update()
    {
        var def = Definition(id: "s", nodes: [Node(0, "a", "step a")]);
        var (reporter, bus) = NewReporter(def);

        await reporter.OnStepFinishedAsync(new StepResult
        {
            Node = def.Nodes[0],
            DisplayName = "step a",
            Status = StepStatus.Passed,
            Effects =
            [
                new ResourceEffect
                {
                    Verb = LifecycleVerb.Create,
                    Identity = new ResourceIdentity(typeof(string), "jane"),
                    StepId = "a",
                    StepDisplayName = "step a",
                },
            ],
        });

#pragma warning disable TPEXP // StandardOutputProperty is experimental in MTP 1.9.1.
        var node = Assert.Single(bus.Nodes);
        var output = Assert.Single(node.Properties.OfType<StandardOutputProperty>());
        Assert.Contains("Create", output.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("jane", output.StandardOutput, StringComparison.Ordinal);
#pragma warning restore TPEXP
    }
```

(`new ResourceIdentity(typeof(string), "jane").ToString()` renders `String:jane`, so the `[resource] Create String:jane` line satisfies both `Contains` assertions.)

- [ ] **Step 5: Run test to verify it fails**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj --filter "FullyQualifiedName~surfaces_resource_effects"`
Expected: FAIL — effects are not surfaced yet.

- [ ] **Step 6 (reporter): surface effects**

In `src/Raun.Mtp/RaunStepReporter.cs`, extend the standard-output construction. The simplest non-disruptive change: include effect lines in the same `StandardOutputProperty` that `AddOutput` builds. Replace `AddOutput` so it appends effect lines after the logs (and emits output when there are logs **or** effects):

```csharp
    static void AddOutput(TestNode testNode, StepResult result)
    {
        if (result.Logs.Count == 0 && result.Effects.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder();
        foreach (var line in result.Logs)
        {
            builder.AppendLine(line);
        }

        foreach (var effect in result.Effects)
        {
            builder.AppendLine(
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "[resource] {0} {1}",
                    effect.Verb,
                    effect.Identity));
        }

#pragma warning disable TPEXP // StandardOutputProperty is experimental in MTP 1.9.1 but stable enough for v1.
        testNode.Properties.Add(new StandardOutputProperty(builder.ToString()));
#pragma warning restore TPEXP
    }
```

(`result.Identity.ToString()` renders `Type:Key`, e.g. `User:jane`.)

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test test/Raun.Mtp.Test/Raun.Mtp.Test.csproj`
Expected: PASS — the new test plus all existing reporter tests (confirm the logs-only test still passes; output now also fires when only effects exist).

- [ ] **Step 8: Full suite + build green**

Run: `dotnet build` then `dotnet test` (all projects).
Expected: 0 warnings, 0 errors; every test passes.

- [ ] **Step 9: Commit**

```bash
jj describe -m "feat: exercise resource effects in the sample and surface them via the MTP reporter"
jj new
```

---

## Final verification (after Task 11)

- [ ] `dotnet build` → 0 warnings, 0 errors.
- [ ] `dotnet test` → all projects green (Raun.Test, Raun.Generator.Test, Raun.Mtp.Test).
- [ ] `dotnet run --project samples/AppointmentTests/AppointmentTests.csproj -- --list-tests` still lists the per-step nodes; a full run passes.
- [ ] Spot-check the accepted resource snapshot one more time for the injected calls.
- [ ] Confirm `git`/`jj` history is a clean sequence of green, single-purpose commits with no tooling trailers.

## Spec coverage check (C1 scope)

| C1 spec item | Task(s) |
|---|---|
| Symbolic resources with type-safe identity (`IResource<TSelf>`) | 2 |
| Identity resolver chain (KeyFor → selector → `IResourceIdentity` → value-equality); `with`-edit keeps key | 3 |
| `ResourceEffect` model (verb, identity, data, step, timestamp) | 4 |
| Imperative `ctx.Resources.*` substrate (Create/Load/Read/Edit/Delete + key-based Load); claims dedup by identity, strongest verb | 5 |
| `ScenarioContext.Resources` surface | 5 |
| Effect stream through `StepResult` / `IStepObserver` | 6, 11 |
| Explicit role attributes; no defaults | 7 |
| Generator lowers roles (param + return) to `ctx.Resources.*` | 8 |
| RAUN009 in all three registration sites + fires on unannotated resource param/return | 9 |
| Behavioral generator tests + Verify snapshot | 8, 10 |
| End-to-end: roles in the sample, effects surfaced | 11 |

**Explicitly deferred to C2** (locking & scheduling): `IResourceLockManager`, async RW gate, `ScenarioLockScope`/`BeginScenarioScope`/`LockAsync`, `Access`/`LockMode`, 2PL + wound-wait + retry cap, static claim catalog + fast path, scheduler/session integration & cross-scenario execution, `[Resource]`/`[ResourceKey]` key-projection codegen, `ISingletonResource<T>` + `[Requires<T>]`, the HTML report resource lifeline (feature B).
