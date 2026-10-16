# Non-generic scenario and step bases

## Goal

Write suites without shared state using Scenarios, Given, When, Then, and Phase.
Use the generic forms when steps need a typed world. The user approved this inheritance direction in chat.

## API and ownership

- Scenarios owns the shared Steps<T>() authoring method. Scenarios<TWorld> derives from it.
- Phase owns Context, nested Steps<T>(), and scope binding. Phase<TWorld> derives from it and adds World.
- Non-generic Given, When, and Then derive from Phase and carry their phase names.
- Existing generic phase classes continue to derive from Phase<TWorld>.
- A step without a typed world may run in either kind of suite. A step that declares TWorld must match the suite's world.
- Legacy generic NoWorld forms stay supported. They remain explicitly bound to NoWorld.

The existing ScenarioScope<TWorld> keeps one step cache and DI construction path. Its common phase binding
may use a small internal scope interface; it must reject a typed phase bound to a different world before use.
There is no second scheduler or discovery path. Generated worldless graphs may continue using NoWorld internally.

The generator must distinguish a world-independent phase from an explicit Phase<NoWorld>.
It recognizes common Steps<T>() methods on generic and non-generic bases. Existing world mismatch,
phase naming, receiver shape, resource, and case checks still apply.

## Alternatives

- Non-generic aliases deriving from generic NoWorld bases: rejected by the user; common behavior belongs in the non-generic base.
- Duplicate runtime paths for worldless suites: unnecessary; keep the existing scope and scheduler.
- Make generic phase classes derive from their non-generic named phase: would lose the existing Phase<TWorld> hierarchy or repeat typed-world behavior. Keep both built-in forms as small phase-name wrappers.
- Allow arbitrary world mismatches after broadening Steps<T>: rejected; preserve generator errors and runtime binding checks.

## Verification

Check API inheritance, Context, instance caching, nested groups, DI, typed-world access, mixed worldless/typed steps,
wrong-world rejection, named cases, titles and resources. Existing generic suites and snapshots must keep working.
Move the in-memory sample and starter documentation to the non-generic forms. Keep the world-based samples generic.
