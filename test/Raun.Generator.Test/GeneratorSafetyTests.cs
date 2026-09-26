using System;
using Raun.Generator;
using Raun.Generator.Lowering;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>The parse/emit safety helpers turn an unexpected throw into a reportable error instead
/// of crashing the generator, and pass successful results through untouched.</summary>
public class GeneratorSafetyTests
{
    [Fact]
    public void SafeParse_wraps_an_exception_as_an_error_result()
    {
        var result = GeneratorSafety.SafeParse(
            () => throw new InvalidOperationException("boom"), "Scenarios.cs", 12);

        Assert.Null(result.Scenario);
        Assert.Equal("Scenarios.cs", result.File);
        Assert.Equal(12, result.Line);
        Assert.Contains("boom", result.Error);
        Assert.Contains("InvalidOperationException", result.Error);
    }

    [Fact]
    public void SafeParse_passes_a_successful_parse_through()
    {
        var scenario = new ParsedScenario { DisplayName = "ok" };

        var result = GeneratorSafety.SafeParse(
            () => ParseOutcome.Lowered(scenario), "x", 1);

        Assert.Same(scenario, result.Scenario);
        Assert.Empty(result.Diagnostics);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SafeParse_passes_diagnostics_through()
    {
        var diagnostic = new ScenarioDiagnostic(
            "RAUN002", EquatableArray<string>.Empty, "Scenarios.cs", 120, 10, new SourceSpan("Scenarios.cs", 6, 8, 6, 18));

        var result = GeneratorSafety.SafeParse(() => ParseOutcome.Refused(new[] { diagnostic }), "x", 1);

        Assert.Null(result.Scenario);
        Assert.Equal([diagnostic], result.Diagnostics);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SafeEmit_wraps_an_exception_as_an_error()
    {
        var (source, error) = GeneratorSafety.SafeEmit(() => throw new InvalidOperationException("kaboom"));

        Assert.Null(source);
        Assert.Contains("kaboom", error);
    }

    [Fact]
    public void SafeEmit_passes_emitted_source_through()
    {
        var (source, error) = GeneratorSafety.SafeEmit(() => "generated");

        Assert.Equal("generated", source);
        Assert.Null(error);
    }
}
