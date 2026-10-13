using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Raun.Coverage.Projects;

/// <summary>Reads the existing Microsoft collector's scope settings.</summary>
public static class CollectorSettingsReader
{
    public static CollectorScope Read(Stream stream)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = reader.ReadToEnd();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (text.TrimStart().StartsWith('<'))
        {
            using var xmlReader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(xmlReader);
            var configuration = document.Root?.Name == "Configuration" ? document.Root
                : document.Descendants("DataCollector").FirstOrDefault(node => (string?)node.Attribute("friendlyName") == "Code Coverage")?.Element("Configuration");
            if (configuration is null) { throw new InvalidDataException("No Code Coverage configuration was found."); }
            FlattenXml(configuration, "Configuration", values);
        }
        else
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.TryGetProperty("codeCoverage", out var coverage)) { root = coverage; }
            FlattenJson(root, "", values);
        }
        return FromConfiguration(key => values.GetValueOrDefault(key), "Configuration");
    }

    public static CollectorScope FromConfiguration(Func<string, string?> get, string prefix = "codeCoverage:Configuration")
    {
        ArgumentNullException.ThrowIfNull(get);
        string? Value(string suffix) => get(prefix + ":" + suffix);
        string[] List(string group, string kind)
        {
            var values = new List<string>();
            for (var i = 0; ; i++)
            {
                var value = Value("CodeCoverage:" + group + ":" + kind + ":" + i);
                if (value is null) { break; }
                values.Add(value);
            }
            return values.ToArray();
        }
        return new CollectorScope
        {
            IncludeTestAssembly = bool.TryParse(Value("IncludeTestAssembly"), out var include) && include,
            MergeDefaultAttributes = !bool.TryParse(Value("CodeCoverage:Attributes:mergeDefaults"), out var merge) || merge,
            ModuleIncludes = List("ModulePaths", "Include"), ModuleExcludes = List("ModulePaths", "Exclude"),
            SourceIncludes = List("Sources", "Include"), SourceExcludes = List("Sources", "Exclude"),
            FunctionIncludes = List("Functions", "Include"), FunctionExcludes = List("Functions", "Exclude"),
            AttributeIncludes = List("Attributes", "Include"), AttributeExcludes = List("Attributes", "Exclude"),
            CompanyIncludes = List("CompanyNames", "Include"), CompanyExcludes = List("CompanyNames", "Exclude"),
            PublicKeyIncludes = List("PublicKeyTokens", "Include"), PublicKeyExcludes = List("PublicKeyTokens", "Exclude"),
        };
    }

    private static void FlattenJson(JsonElement element, string path, Dictionary<string, string> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                FlattenJson(property.Value, path.Length == 0 ? property.Name : path + ":" + property.Name, values);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var value in element.EnumerateArray()) { FlattenJson(value, path + ":" + index++, values); }
        }
        else { values[path] = element.ToString(); }
    }

    private static void FlattenXml(XElement element, string path, Dictionary<string, string> values)
    {
        foreach (var attribute in element.Attributes()) { values[path + ":" + attribute.Name.LocalName] = attribute.Value; }
        if (element.Name.LocalName is "Include" or "Exclude")
        {
            var index = 0;
            foreach (var child in element.Elements()) { values[path + ":" + index++] = child.Value; }
        }
        else if (element.HasElements)
        {
            foreach (var child in element.Elements()) { FlattenXml(child, path + ":" + child.Name.LocalName, values); }
        }
        else { values[path] = element.Value; }
    }
}
