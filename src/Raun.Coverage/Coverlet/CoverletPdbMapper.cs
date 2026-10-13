using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis.Text;
using Raun.Coverage.MicrosoftXml;

namespace Raun.Coverage.Coverlet;

// JSON has line counters but no columns or build identity. A linked portable PDB supplies
// source checksums and ranges; it cannot turn aggregated line hits into per-statement hits.
internal static class CoverletPdbMapper
{
    public static CoverageModule Map(CoverageModule module, string outputPath)
    {
        try
        {
            using var assembly = File.OpenRead(outputPath);
            using var pe = new PEReader(assembly);
            foreach (var entry in pe.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    using var embedded = pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                    return Map(module, embedded.GetMetadataReader(), pe);
                }
                if (entry.Type != DebugDirectoryEntryType.CodeView) { continue; }
                var pdbPath = Path.ChangeExtension(outputPath, ".pdb");
                if (!File.Exists(pdbPath)) { continue; }
                using var stream = File.OpenRead(pdbPath);
                using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
                var reader = provider.GetMetadataReader();
                if (reader.DebugMetadataHeader is not { } header) { return module; }
                var id = new BlobContentId(header.Id);
                var view = pe.ReadCodeViewDebugDirectoryData(entry);
                if (id.Guid != view.Guid || id.Stamp != entry.Stamp) { return module; }
                return Map(module, reader, pe);
            }
        }
        catch (IOException) { }
        catch (BadImageFormatException) { }
        return module;
    }

    private static CoverageModule Map(CoverageModule module, MetadataReader reader, PEReader pe)
    {
        var sources = reader.Documents.ToDictionary(handle => handle, handle =>
        {
            var document = reader.GetDocument(handle);
            var algorithm = reader.GetGuid(document.HashAlgorithm);
            var name = algorithm == new Guid("8829d00f-11b8-4213-878b-770e8597ac16") ? "SHA256"
                : algorithm == new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460") ? "SHA1" : "";
            return new CoverageSource(reader.GetString(document.Name), name, Convert.ToHexString(reader.GetBlobBytes(document.Hash)));
        });
        var points = reader.MethodDebugInformation.SelectMany(handle => reader.GetMethodDebugInformation(handle).GetSequencePoints())
            .Where(point => !point.IsHidden && sources.ContainsKey(point.Document))
            .Select(point => (Source: sources[point.Document], point.StartLine, point.StartColumn, point.EndLine, point.EndColumn))
            .Distinct().ToArray();
        var ranges = new List<CoverageRange>();
        foreach (var line in module.Lines.GroupBy(line => (line.Path, line.Line)))
        {
            var matches = points.Where(point => SamePath(point.Source.Path, line.Key.Path) && point.StartLine == line.Key.Line).ToArray();
            var positive = line.Any(item => item.Hits > 0);
            foreach (var point in matches)
            {
                ranges.Add(new CoverageRange(point.Source, new LinePosition(point.StartLine - 1, point.StartColumn - 1),
                    new LinePosition(point.EndLine - 1, point.EndColumn - 1),
                    !positive ? CoverageHit.No : matches.Length == 1 ? CoverageHit.Yes : CoverageHit.Partial));
            }
        }
        return module with { Id = Convert.ToHexString(reader.DebugMetadataHeader!.Id.AsSpan()), Ranges = ranges, Methods = CoverletIlReader.Read(pe, reader, sources) };
    }

    private static bool SamePath(string left, string right)
        => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
