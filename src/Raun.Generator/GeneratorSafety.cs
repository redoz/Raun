using System;
using Raun.Generator.Lowering;

namespace Raun.Generator;

/// <summary>The outcome of safely parsing one scenario: the parsed <see cref="Scenario"/>; or a
/// <see cref="Rejection"/> (the parser declined a statement, reported as RAUN017); or an
/// <see cref="Error"/> (exception text) plus the originating method's <see cref="File"/>/<see cref="Line"/>
/// to report as RAUN000.</summary>
internal readonly record struct ScenarioResult(
    ParsedScenario? Scenario, ParseRejection? Rejection, string? Error, string? File, int Line);

/// <summary>
/// Wraps the generator's parse and emit stages so an unexpected throw becomes a RAUN000 diagnostic
/// instead of crashing the generator (CS8785). Delegate-driven, so the wrapping behaviour is
/// unit-testable without forcing the real parser/emitter to throw.
/// </summary>
internal static class GeneratorSafety
{
    public static ScenarioResult SafeParse(Func<ParseOutcome> parse, string? file, int line)
    {
        try
        {
            var outcome = parse();
            return new ScenarioResult(outcome.Scenario, outcome.Rejection, null, null, 0);
        }
        catch (Exception ex)
        {
            return new ScenarioResult(null, null, Describe(ex), file, line);
        }
    }

    public static (string? Source, string? Error) SafeEmit(Func<string> emit)
    {
        try
        {
            return (emit(), null);
        }
        catch (Exception ex)
        {
            return (null, Describe(ex));
        }
    }

    /// <summary>A compact, single-line description of an exception for a diagnostic message.</summary>
    public static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;
}
