using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.Text;

namespace Raun.Coverage.MicrosoftXml;

internal static class MicrosoftCoverageXmlReader
{
    public static CoverageObservations Read(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        if (document.Root?.Name != "results" || document.Root.Element("modules") is not { } modules)
        {
            throw new InvalidDataException("Classified coverage requires Microsoft XML, not cobertura or binary coverage.");
        }
        var result = new List<CoverageModule>();
        foreach (var module in modules.Elements("module"))
        {
            var sources = (module.Element("source_files")?.Elements("source_file") ?? [])
                .ToDictionary(source => Required(source, "id"), source => new CoverageSource(
                    Required(source, "path"), Required(source, "checksum_type"), Required(source, "checksum")), StringComparer.Ordinal);
            var ranges = new List<CoverageRange>();
            foreach (var range in module.Element("functions")?.Descendants("range") ?? [])
            {
                if (!sources.TryGetValue(Required(range, "source_id"), out var source))
                {
                    throw new InvalidDataException("Coverage range refers to an unknown source document.");
                }
                var start = new LinePosition(Number(range, "start_line") - 1, Number(range, "start_column") - 1);
                var end = new LinePosition(Number(range, "end_line") - 1, Number(range, "end_column") - 1);
                var hit = Required(range, "covered") switch
                {
                    "yes" => CoverageHit.Yes, "no" => CoverageHit.No, "partial" => CoverageHit.Partial,
                    _ => throw new InvalidDataException("Unknown coverage range state."),
                };
                ranges.Add(new CoverageRange(source, start, end, hit));
            }
            result.Add(new CoverageModule(Required(module, "id"), Required(module, "name"), ranges));
        }
        return new CoverageObservations(result);
    }

    private static int Number(XElement element, string name)
        => int.TryParse(Required(element, name), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number : throw new InvalidDataException("Invalid coverage source position.");

    private static string Required(XElement element, string name)
        => element.Attribute(name)?.Value ?? throw new InvalidDataException("Missing coverage attribute: " + name);
}
