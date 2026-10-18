using System.Text.Json;
using System.Text.RegularExpressions;

namespace Raun.Coverage.Projects;

/// <summary>Reads the scope selected by Coverlet MTP, including its CLI precedence.</summary>
public static class CoverletSettingsReader
{
    private static readonly string[] ConfigExcludes = ["[coverlet.*]*"];
    private static readonly string[] DefaultAttributes = ["ExcludeFromCodeCoverage", "ExcludeFromCodeCoverageAttribute", "GeneratedCodeAttribute", "CompilerGeneratedAttribute"];
    private static readonly string[] BaselineExcludes = ["[coverlet.*]*", "[Microsoft.VisualStudio.TestPlatform.*]*", "[testhost*]*", "[ReportGenerator.*]*"];

    public static CollectorScope Read(Stream stream)
        => FromValues(Values(stream), _ => null, _ => false, true);

    public static CollectorScope Read(string modulePath, string invocationDirectory, Func<string, string[]?> arguments, Func<string, bool> isSet)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(isSet);
        var directory = Path.GetDirectoryName(modulePath)!;
        var explicitPath = arguments("--config-file") ?? arguments("config-file");
        Dictionary<string, string>? values = null;
        if (explicitPath is { Length: > 0 })
        {
            using var stream = File.OpenRead(Path.GetFullPath(explicitPath[0], invocationDirectory));
            values = Values(stream, testConfig: true);
        }
        else
        {
            var specific = Path.Combine(directory, Path.GetFileNameWithoutExtension(modulePath) + ".testconfig.json");
            var general = Path.Combine(directory, "testconfig.json");
            var selected = File.Exists(specific) ? specific : File.Exists(general) ? general : null;
            if (selected is not null)
            {
                using var stream = File.OpenRead(selected);
                values = Values(stream, testConfig: true);
            }
            var legacy = Path.Combine(directory, "coverlet.mtp.appsettings.json");
            if (values is null && File.Exists(legacy))
            {
                using var stream = File.OpenRead(legacy);
                values = Values(stream);
            }
        }
        return FromValues(values ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), arguments, isSet, values is not null);
    }

    private static Dictionary<string, string>? Values(Stream stream, bool testConfig = false)
    {
        using var json = JsonDocument.Parse(stream);
        var root = json.RootElement;
        static JsonElement? Property(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject().Where(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).Select(item => (JsonElement?)item.Value).FirstOrDefault() : null;
        if (testConfig)
        {
            if (Property(root, "platformOptions") is not { } platform || Property(platform, "Coverlet") is not { } coverlet) { return null; }
            root = coverlet;
        }
        else if (Property(root, "Coverlet") is { } section) { root = section; }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False)
            { result[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString(); }
        }
        return result;
    }

    private static CollectorScope FromValues(Dictionary<string, string>? values, Func<string, string[]?> arguments, Func<string, bool> isSet, bool hasFile)
    {
        string[] List(string name) => (values?.GetValueOrDefault(name) ?? "").Split([',', '\u0001'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool Flag(string option, string name) => isSet(option) || bool.TryParse(values?.GetValueOrDefault(name), out var value) && value;
        var exclude = arguments("coverlet-exclude") is { } commandExcludes ? BaselineExcludes.Concat(commandExcludes).ToArray()
            : hasFile ? ConfigExcludes.Concat(List("Exclude")).ToArray() : BaselineExcludes;
        var attributes = arguments("coverlet-exclude-by-attribute") is { } commandAttributes ? DefaultAttributes.Concat(commandAttributes).ToArray()
            : hasFile ? List("ExcludeByAttribute") : DefaultAttributes;
        return new CollectorScope
        {
            IncludeTestAssembly = Flag("coverlet-include-test-assembly", "IncludeTestAssembly"),
            CoverletSingleHit = Flag("coverlet-single-hit", "SingleHit"),
            CoverletIncludes = arguments("coverlet-include") ?? List("Include"), CoverletExcludes = exclude,
            CoverletSourceExcludes = arguments("coverlet-exclude-by-file") ?? List("ExcludeByFile"),
            AttributeExcludes = attributes.Select(name => "(^|\\.)" + Regex.Escape(name) + "(Attribute)?$").ToArray(),
        };
    }
}
