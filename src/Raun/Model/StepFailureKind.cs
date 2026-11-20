namespace Raun.Model;

/// <summary>What kind of failure a failed step is. See <see cref="StepResult.FailureKind"/>.</summary>
public enum StepFailureKind
{
    /// <summary>An assertion did not hold: the system under test gave the wrong answer.</summary>
    Assertion,

    /// <summary>Anything else was thrown: the step could not get as far as an answer.</summary>
    Error,

    /// <summary>The step, or the scenario while this step held it, ran out of time.</summary>
    Timeout,
}

/// <summary>Tells an assertion from an error by what a failed step threw. A timeout is not decided
/// here: see <see cref="StepResult.TimedOut"/>.</summary>
internal static class StepFailure
{
    /// <summary>
    /// Assertion libraries share no base type, so their exceptions are known by name:
    /// <c>XunitException</c> (xunit.v3.assert), <c>AssertionException</c> (NUnit and others),
    /// <c>AssertionFailedException</c> (FluentAssertions), <c>ShouldAssertException</c> (Shouldly),
    /// <c>AssertFailedException</c> (MSTest).
    /// </summary>
    private static readonly string[] AssertionExceptionNames =
    [
        "XunitException",
        "AssertionException",
        "AssertionFailedException",
        "ShouldAssertException",
        "AssertFailedException",
    ];

    public static StepFailureKind Classify(Exception? exception) => exception switch
    {
        null => StepFailureKind.Error,
        _ when IsAssertion(exception) => StepFailureKind.Assertion,
        _ => StepFailureKind.Error,
    };

    private static bool IsAssertion(Exception exception)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (IsAssertionName(type.Name))
            {
                return true;
            }
        }

        foreach (var contract in exception.GetType().GetInterfaces())
        {
            if (IsAssertionName(contract.Name))
            {
                return true;
            }
        }

        return false;

        static bool IsAssertionName(string name)
        {
            foreach (var known in AssertionExceptionNames)
            {
                if (name.EndsWith(known, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
