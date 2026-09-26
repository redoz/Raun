# xUnit.net v3 (3.2.2) Extensibility API Reference — Scenario Graph Extension

> Reflection-verified against the installed 3.2.2 assemblies (and source @ commit
> `728c1dce012cd82193035dddfeaba184baaa88c6`). Used to build the `Raun.Xunit` adapter (Phase 4).
> Items flagged "double-check" must be confirmed against the live runner during Phase 4.

## Critical namespace facts
- Abstraction + message **interfaces** live in `Xunit.Sdk` (assembly `xunit.v3.common`):
  `ITestCase`, `ITest`, `ITestPassed`, `IXunitSerializable`, `IXunitSerializationInfo`,
  `ExplicitOption`, `FailureCause`, `UniqueIDGenerator`, `TestIntrospectionHelper`.
- xUnit-specific objects, the message bus, **concrete** message classes, and runners live in
  `Xunit.v3` (assembly `xunit.v3.core`): `IXunitTestCase`, `ISelfExecutingXunitTestCase`,
  `IMessageBus`, `XunitTestCase`, `TestPassed`/`TestFailed`/`TestSkipped`/`TestStarting`/
  `TestFinished`, `XunitRunnerHelper`, `ExceptionAggregator`, `RunSummary`, `XunitTest`.
- `FactAttribute` is in plain `Xunit` (assembly `xunit.v3.core`).
- There are TWO sets of `TestPassed`/etc.: use the `Xunit.v3.*` ones from a test case (NOT the
  `Xunit.Runner.Common.*` ones).
- The message bus concretes (`Xunit.Internal.MessageBus`/`SynchronousMessageBus`) are internal —
  you never construct one; the pipeline hands you an `IMessageBus`.

## A. Discovery
```csharp
namespace Xunit.v3;
public interface IXunitTestCaseDiscoverer
{
    ValueTask<IReadOnlyCollection<IXunitTestCase>> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions,
        IXunitTestMethod testMethod,
        IFactAttribute factAttribute);
}

[AttributeUsage(AttributeTargets.Class)]
public class XunitTestCaseDiscovererAttribute : Attribute   // Type-based in v3 (not v2 strings)
{
    public XunitTestCaseDiscovererAttribute(Type type);
    public Type Type { get; }
}
```
Custom fact attribute: derive from `FactAttribute` (implements `IFactAttribute`) and decorate it
with `[XunitTestCaseDiscoverer(typeof(YourDiscoverer))]`. The attribute-driven path is sufficient;
no `ITestFrameworkDiscoverer` needed.

`FactAttribute` ctor: `FactAttribute([CallerFilePath] string sourceFilePath = null,
[CallerLineNumber] int sourceLineNumber = -1)` — capture source location automatically.

Recommended discovery body (mirrors built-in `FactDiscoverer`):
```csharp
var details = TestIntrospectionHelper.GetTestCaseDetails(discoveryOptions, testMethod, factAttribute);
var testCase = new ScenarioTestCase(details.ResolvedTestMethod, details.TestCaseDisplayName,
    details.UniqueID, details.Explicit, sourceFilePath: details.SourceFilePath,
    sourceLineNumber: details.SourceLineNumber, timeout: details.Timeout);
return new([testCase]);
```
`TestIntrospectionHelper.GetTestCaseDetails` is public in `Xunit.Sdk`. (double-check exact member
names on the returned `details` via IntelliSense.)

## B. Test case
```csharp
namespace Xunit.v3;
public interface ISelfExecutingXunitTestCase : IXunitTestCase
{
    ValueTask<RunSummary> Run(
        ExplicitOption explicitOption,        // Xunit.Sdk enum: Off=0,On=1,Only=2
        IMessageBus messageBus,
        object?[] constructorArguments,
        ExceptionAggregator aggregator,        // Xunit.v3 STRUCT
        CancellationTokenSource cancellationTokenSource);
}
```
`RunSummary` (struct, `Xunit.v3`): mutable `int Total, Failed, Skipped, NotRun; decimal Time;`
plus `void Aggregate(RunSummary other)`.

`XunitTestCase` base ctor (the only practical base; derive from it):
```csharp
public XunitTestCase();   // [Obsolete] deserializer ctor — call from derived parameterless ctor
public XunitTestCase(IXunitTestMethod testMethod, string testCaseDisplayName, string uniqueID,
    bool @explicit, Type[] skipExceptions = null, string skipReason = null, Type skipType = null,
    string skipUnless = null, string skipWhen = null,
    Dictionary<string, HashSet<string>> traits = null, object[] testMethodArguments = null,
    string sourceFilePath = null, int? sourceLineNumber = null, int? timeout = null);
```
Serialization: override `protected virtual void Serialize(IXunitSerializationInfo info)` /
`Deserialize(...)`, calling `base` first. v3 serializes test cases, so any added state must
round-trip. Use the generic extension helpers in `Xunit.Sdk.XunitSerializationInfoExtensions`:
`info.AddValue<T>(key, value)` / `info.GetValue<T>(key)`.

Multiple visible tests per case: either override `CreateTests()` to return N `IXunitTest` (runner
then invokes the method once per test), OR — for pre-computed step results — implement
`ISelfExecutingXunitTestCase` and emit a per-test message quartet per step inside `Run`. Raun uses
the self-executing path.

`XunitTest` (constructable, `Xunit.v3`) — use the `testIndex` ctor for a derived per-test UniqueID:
```csharp
public XunitTest(IXunitTestCase testCase, IXunitTestMethod testMethod, bool? @explicit,
    string skipReason, Type skipType, string skipUnless, string skipWhen,
    string testDisplayName, int testIndex,
    IReadOnlyDictionary<string,IReadOnlyCollection<string>> traits, int? timeout,
    object[] testMethodArguments);
```

## C. Result reporting / message bus
```csharp
namespace Xunit.v3;
public interface IMessageBus : IDisposable { bool QueueMessage(IMessageSinkMessage message); }
```
Per visible test, canonical order: `TestStarting` → one of
`TestPassed|TestFailed|TestSkipped|TestNotRun` → `TestFinished`.
(`TestClassConstruction*` only if you actually instantiate the user's test class — skip for a
self-executing case that computes results itself.)

Every test-level message needs all six UniqueIDs (inherited via the message base chain):
`AssemblyUniqueID, TestCollectionUniqueID, TestClassUniqueID, TestMethodUniqueID,
TestCaseUniqueID, TestUniqueID`, plus result messages carry
`decimal ExecutionTime; DateTimeOffset FinishTime; string Output; string[] Warnings`.

Key concrete messages (parameterless ctor + settable props):
- `TestStarting { bool Explicit; DateTimeOffset StartTime; string TestDisplayName; int Timeout;
  IReadOnlyDictionary<string,IReadOnlyCollection<string>> Traits; +6 UniqueIDs }`
- `TestPassed` — base members only.
- `TestSkipped { string Reason; }`
- `TestFailed { FailureCause Cause; int[] ExceptionParentIndices; string[] ExceptionTypes;
  string[] Messages; string[] StackTraces; ... }` — use the helper:
  `static ITestFailed TestFailed.FromException(Exception ex, string asmId, string colId,
  string clsId, string methId, string caseId, string testId, decimal executionTime,
  string output, string[] warnings, DateTimeOffset? finishTime = null)`.
- `TestFinished { IReadOnlyDictionary<string,TestAttachment> Attachments; ... }` — use
  `TestFinished.EmptyAttachments` (double-check it's public; else pass an empty dictionary).

UniqueIDs at runtime come off the model (no manual hashing):
`TestCollection.TestAssembly.UniqueID`, `TestCollection.UniqueID`, `TestClass?.UniqueID`,
`TestMethod?.UniqueID`, `UniqueID` (the case), and per step
`UniqueIDGenerator.ForTest(caseUniqueID, stepIndex)`.

Higher-level helpers (`Xunit.v3.XunitRunnerHelper`, static): `FailTest`, `FailTestCases`,
`SkipTestCases`, `RunXunitTestCase` — send the full quartet for you.

## D. Double-check during Phase 4 (against the live runner)
1. Exact member names on `TestIntrospectionHelper.GetTestCaseDetails(...)` return value.
2. Whether `TestCaseStarting`/`TestCaseFinished` must be emitted inside `Run` (likely the
   framework wraps your `Run`, so emit only the per-test quartet; add case messages if the case
   doesn't show up).
3. `TestFinished.EmptyAttachments` visibility.
4. Use plain `[Obsolete(...)]` (not `error: true`) on the deserializer ctor so the deserializer can
   call it.

## Reference implementations to mirror (source @ pinned commit)
- `src/xunit.v3.core/Framework/FactDiscoverer.cs`
- `src/xunit.v3.core/ObjectModel/ExecutionErrorTestCase.cs` (minimal `XunitTestCase` subclass +
  Serialize/Deserialize pattern)
- `src/xunit.v3.core/Runners/TestRunnerBase.cs` (per-test message ceremony)
- `src/xunit.v3.core/Runners/XunitTestMethodRunnerBase.cs` (`ISelfExecutingXunitTestCase` dispatch)
