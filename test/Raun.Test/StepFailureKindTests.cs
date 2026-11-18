using Raun.Model;
using Xunit;
using Xunit.Sdk;

namespace Raun.Test;

/// <summary>
/// What kind of failure a failed step is — an assertion that did not hold, a timeout, or anything
/// else — is decided once, in the runtime, so every host and report says the same.
/// </summary>
public class StepFailureKindTests
{
    // Assertion libraries the runtime recognizes without referencing them. Matching is by type
    // name, so standing in for the real type is exactly what it sees.
#pragma warning disable CA1032, CA1064, CA1812, CA1040, CA1711, RCS1194 // stand-in types, constructed reflectively below
    private sealed class AssertionFailedException : Exception;      // FluentAssertions
    private sealed class ShouldAssertException : Exception;         // Shouldly
    private sealed class AssertFailedException : Exception;         // MSTest
    private class AssertionException : Exception;                   // NUnit
    private sealed class MultipleAssertException : AssertionException;
    private interface IAssertionException;
    private sealed class MarkedByInterface : Exception, IAssertionException;
#pragma warning restore CA1032, CA1064, CA1812, CA1040, CA1711, RCS1194

    private static readonly ScenarioNode Node = new()
    {
        Index = 0, StepId = "s", Phase = "Then", OperationName = "Op", DisplayNameTemplate = "op", DependsOn = [],
        Invoke = (_, _) => Task.FromResult<object?>(null),
    };

    private static StepResult Result(StepStatus status, Exception? exception = null)
        => new() { Node = Node, Status = status, StartedAt = default, Exception = exception };

    [Theory]
    [InlineData(typeof(AssertionFailedException))]
    [InlineData(typeof(ShouldAssertException))]
    [InlineData(typeof(AssertFailedException))]
    [InlineData(typeof(AssertionException))]
    [InlineData(typeof(MultipleAssertException))]   // by base type
    [InlineData(typeof(MarkedByInterface))]         // by interface
    public void An_assertion_library_s_exception_is_an_assertion(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.Equal(StepFailureKind.Assertion, Result(StepStatus.Failed, exception).FailureKind);
    }

    [Fact]
    public void An_xunit_assertion_is_an_assertion()
        => Assert.Equal(
            StepFailureKind.Assertion,
            Result(StepStatus.Failed, Assert.Throws<EqualException>(() => Assert.Equal(1, 2))).FailureKind);

    [Fact]
    public void A_timeout_is_a_timeout()
        => Assert.Equal(StepFailureKind.Timeout, Result(StepStatus.Failed, new TimeoutException()).FailureKind);

    [Fact]
    public void Any_other_exception_is_an_error()
        => Assert.Equal(StepFailureKind.Error, Result(StepStatus.Failed, new InvalidOperationException()).FailureKind);

    [Fact]
    public void A_failure_without_an_exception_is_an_error()
        => Assert.Equal(StepFailureKind.Error, Result(StepStatus.Failed).FailureKind);

    [Theory]
    [InlineData(StepStatus.Passed)]
    [InlineData(StepStatus.Skipped)]
    [InlineData(StepStatus.NotTaken)]
    public void A_step_that_did_not_fail_has_no_failure_kind(StepStatus status)
        => Assert.Null(Result(status).FailureKind);
}
