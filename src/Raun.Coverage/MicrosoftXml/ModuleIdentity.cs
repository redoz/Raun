using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Raun.Coverage.MicrosoftXml;

internal static class ModuleIdentity
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Unavailable or incompatible build outputs cannot establish coverage identity.")]
    public static bool Matches(string outputPath, CoverageModule module)
    {
        try
        {
            using var stream = File.OpenRead(outputPath);
            using var pe = new PEReader(stream);
            foreach (var entry in pe.ReadDebugDirectory())
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    using var pdb = pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                    return Matches(pdb, module.Id);
                }
                if (entry.Type == DebugDirectoryEntryType.CodeView)
                {
                    var path = Path.ChangeExtension(outputPath, ".pdb");
                    if (!File.Exists(path)) { continue; }
                    using var pdbStream = File.OpenRead(path);
                    using var pdb = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                    var header = pdb.GetMetadataReader().DebugMetadataHeader;
                    if (header is null) { return false; }
                    var contentId = new BlobContentId(header.Id);
                    var codeView = pe.ReadCodeViewDebugDirectoryData(entry);
                    return contentId.Guid == codeView.Guid && contentId.Stamp == entry.Stamp && Matches(pdb, module.Id);
                }
            }
        }
        catch (Exception) { return false; }
        return false;
    }

    private static bool Matches(MetadataReaderProvider pdb, string id)
        => pdb.GetMetadataReader().DebugMetadataHeader is { } header
            && string.Equals(Convert.ToHexString(header.Id.AsSpan()), id, StringComparison.OrdinalIgnoreCase);
}
