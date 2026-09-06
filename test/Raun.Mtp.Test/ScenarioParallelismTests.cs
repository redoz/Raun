using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions.CommandLine;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>The degree of scenario parallelism: code sets the suite default, --max-parallel-scenarios overrides per run.</summary>
public class ScenarioParallelismTests
{
    [Fact]
    public void The_option_is_registered_and_takes_exactly_one_argument()
    {
        var provider = new RunOptionsProvider();
        var option = Assert.Single(provider.GetCommandLineOptions(), o => o.Name == "max-parallel-scenarios");
        Assert.Equal(ArgumentArity.ExactlyOne, option.Arity);
    }

    [Theory]
    [InlineData("0", true, 0)]
    [InlineData("1", true, 1)]
    [InlineData("8", true, 8)]
    [InlineData("-1", false, 0)]
    [InlineData("many", false, 0)]
    [InlineData("", false, 0)]
    public void Parses_a_non_negative_integer_only(string argument, bool valid, int expected)
    {
        Assert.Equal(valid, ScenarioParallelism.TryParse([argument], out var degree));
        if (valid)
        {
            Assert.Equal(expected, degree);
        }
    }

    [Fact]
    public async Task The_provider_rejects_what_the_parser_rejects()
    {
        var provider = new RunOptionsProvider();
        var option = provider.GetCommandLineOptions().Single(o => o.Name == "max-parallel-scenarios");

        Assert.True((await provider.ValidateOptionArgumentsAsync(option, ["4"])).IsValid);
        Assert.False((await provider.ValidateOptionArgumentsAsync(option, ["-4"])).IsValid);
        Assert.False((await provider.ValidateOptionArgumentsAsync(option, ["x"])).IsValid);
    }

    [Fact]
    public void Resolve_returns_the_code_default_without_services_or_without_the_option()
    {
        Assert.Equal(3, ScenarioParallelism.Resolve(services: null, codeDefault: 3));
        Assert.Equal(3, ScenarioParallelism.Resolve(new OptionsProvider(null), codeDefault: 3));
    }

    [Fact]
    public void Resolve_lets_the_command_line_override_the_code_default()
    {
        Assert.Equal(1, ScenarioParallelism.Resolve(new OptionsProvider("1"), codeDefault: 3));
        Assert.Equal(0, ScenarioParallelism.Resolve(new OptionsProvider("0"), codeDefault: 3));
    }

    /// <summary>Stands in for MTP's provider: answers ICommandLineOptions with one optional value for the degree.</summary>
    private sealed class OptionsProvider(string? degree) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandLineOptions) ? new Options(degree) : null;

        private sealed class Options(string? degree) : ICommandLineOptions
        {
            public bool IsOptionSet(string optionName) => optionName == "max-parallel-scenarios" && degree is not null;

            public bool TryGetOptionArgumentList(
                string optionName,
                [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string[]? arguments)
            {
                arguments = IsOptionSet(optionName) ? [degree!] : null;
                return arguments is not null;
            }
        }
    }
}
