using System.Diagnostics.CodeAnalysis;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Configurations;
using Microsoft.Testing.Platform.Extensions.OutputDevice;
using Microsoft.Testing.Platform.Logging;
using Microsoft.Testing.Platform.OutputDevice;

namespace Raun.Mtp.Test;

/// <summary>
/// Stands in for the provider Microsoft.Testing.Platform hands a test framework: no command-line
/// option set and no configuration value unless a test sets one, a logger that records warnings and
/// an output device that records what would be printed. It answers the services
/// <see cref="RaunTestFramework"/> asks the platform for; MTP's accessor extensions throw on a
/// missing one.
/// </summary>
internal sealed class TestPlatformServices : IServiceProvider
{
    private readonly Options _options = new();
    private readonly Configuration _configuration = new();
    private readonly LoggerFactory _loggers;

    public TestPlatformServices() => _loggers = new LoggerFactory(Warnings);

    /// <summary>Every message logged at warning level or above, in order.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Every warning shown on the output device (the console, in a real run), in order.</summary>
    public List<string> Printed { get; } = [];

    /// <summary>Sets a command-line option, as if it had been passed with these arguments.</summary>
    public TestPlatformServices WithOption(string name, params string[] arguments)
    {
        _options.Set[name] = arguments;
        return this;
    }

    /// <summary>Sets a configuration value, such as the results directory.</summary>
    public TestPlatformServices WithConfiguration(string key, string value)
    {
        _configuration.Values[key] = value;
        return this;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IOutputDevice))
        {
            return new OutputDevice(Printed);
        }

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
        public Dictionary<string, string[]> Set { get; } = new(StringComparer.Ordinal);

        public bool IsOptionSet(string optionName) => Set.ContainsKey(optionName);

        public bool TryGetOptionArgumentList(string optionName, [NotNullWhen(true)] out string[]? arguments)
            => Set.TryGetValue(optionName, out arguments);
    }

    private sealed class Configuration : IConfiguration
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public string? this[string key] => Values.GetValueOrDefault(key);
    }

    private sealed class OutputDevice(List<string> printed) : IOutputDevice
    {
        public Task DisplayAsync(IOutputDeviceDataProducer producer, IOutputDeviceData data, CancellationToken cancellationToken)
        {
            if (data is WarningMessageOutputDeviceData warning)
            {
                lock (printed)
                {
                    printed.Add(warning.Message);
                }
            }

            return Task.CompletedTask;
        }
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
