using System.Diagnostics.CodeAnalysis;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Configurations;
using Microsoft.Testing.Platform.Logging;

namespace Raun.Mtp.Test;

/// <summary>
/// Stands in for the provider Microsoft.Testing.Platform hands a test framework: no command-line
/// option set, no configuration value, and a logger that records warnings. It answers the three
/// services <see cref="RaunTestFramework"/> asks the platform for; MTP's accessor extensions throw
/// on a missing one.
/// </summary>
internal sealed class TestPlatformServices : IServiceProvider
{
    private readonly Options _options = new();
    private readonly Configuration _configuration = new();
    private readonly LoggerFactory _loggers;

    public TestPlatformServices() => _loggers = new LoggerFactory(Warnings);

    /// <summary>Every message logged at warning level or above, in order.</summary>
    public List<string> Warnings { get; } = [];

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(ICommandLineOptions))
        {
            return _options;
        }

        if (serviceType == typeof(IConfiguration))
        {
            return _configuration;
        }

        return serviceType == typeof(ILoggerFactory) ? _loggers : null;
    }

    private sealed class Options : ICommandLineOptions
    {
        public bool IsOptionSet(string optionName) => false;

        public bool TryGetOptionArgumentList(string optionName, [NotNullWhen(true)] out string[]? arguments)
        {
            arguments = null;
            return false;
        }
    }

    private sealed class Configuration : IConfiguration
    {
        public string? this[string key] => null;
    }

    private sealed class LoggerFactory(List<string> warnings) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => new Logger(warnings);
    }

    private sealed class Logger(List<string> warnings) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lock (warnings)
                {
                    warnings.Add(formatter(state, exception));
                }
            }
        }

        public Task LogAsync<TState>(LogLevel logLevel, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Log(logLevel, state, exception, formatter);
            return Task.CompletedTask;
        }
    }
}
