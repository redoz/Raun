using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions.CommandLine;
using Xunit;

namespace Raun.Mtp.Test;

/// <summary>Finite cleanup deadlines default to 60 seconds and can be overridden per run.</summary>
public class CleanupTimeoutTests
{
    [Fact]
    public void The_option_is_registered_and_takes_exactly_one_argument()
    {
        var provider = new RunOptionsProvider();
        var option = Assert.Single(provider.GetCommandLineOptions(), o => o.Name == "cleanup-timeout-ms");
        Assert.Equal(ArgumentArity.ExactlyOne, option.Arity);
    }

    [Theory]
    [InlineData("0", false, 0)]
    [InlineData("1", true, 1)]
    [InlineData("8", true, 8)]
    [InlineData("60000", true, 60000)]
    [InlineData("2147483647", true, 2147483647)]
    [InlineData("2147483648", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("many", false, 0)]
    [InlineData("", false, 0)]
    public void Parses_a_positive_integer_only(string argument, bool valid, int expected)
    {
        Assert.Equal(valid, CleanupTimeout.TryParse([argument], out var degree));
        if (valid)
        {
            Assert.Equal(expected, degree);
        }
    }

    [Fact]
    public async Task The_provider_rejects_what_the_parser_rejects()
    {
        var provider = new RunOptionsProvider();
        var option = provider.GetCommandLineOptions().Single(o => o.Name == "cleanup-timeout-ms");

        Assert.True((await provider.ValidateOptionArgumentsAsync(option, ["4"])).IsValid);
        Assert.False((await provider.ValidateOptionArgumentsAsync(option, ["-4"])).IsValid);
        Assert.False((await provider.ValidateOptionArgumentsAsync(option, ["0"])).IsValid);
        Assert.False((await provider.ValidateOptionArgumentsAsync(option, ["x"])).IsValid);
    }

    [Fact]
    public void Resolve_returns_the_code_default_without_services_or_without_the_option()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), CleanupTimeout.Resolve(services: null));
        Assert.Equal(TimeSpan.FromSeconds(60), CleanupTimeout.Resolve(new OptionsProvider(null)));
    }

    [Fact]
    public void Resolve_lets_the_command_line_override_the_code_default()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1), CleanupTimeout.Resolve(new OptionsProvider("1")));
        Assert.Equal(TimeSpan.FromMilliseconds(5000), CleanupTimeout.Resolve(new OptionsProvider("5000")));
    }

    /// <summary>Stands in for MTP's provider: answers ICommandLineOptions with one optional value for the degree.</summary>
    private sealed class OptionsProvider(string? degree) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ICommandLineOptions) ? new Options(degree) : null;

        private sealed class Options(string? degree) : ICommandLineOptions
        {
            public bool IsOptionSet(string optionName) => optionName == "cleanup-timeout-ms" && degree is not null;

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
