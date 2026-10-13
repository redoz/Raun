using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Raun.Coverage.MicrosoftXml;
using Xunit;

namespace Raun.Coverage.Test;

public sealed class ModuleIdentityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Xml_module_identity_must_match_evaluated_output_pdb(bool embedded)
    {
        var output = embedded ? typeof(ClassifiedCoverageAnalyzer).Assembly.Location : typeof(ModuleIdentityTests).Assembly.Location;
        using var stream = File.OpenRead(output);
        using var pe = new PEReader(stream);
        var entry = Assert.Single(pe.ReadDebugDirectory(), e => e.Type == (embedded ? DebugDirectoryEntryType.EmbeddedPortablePdb : DebugDirectoryEntryType.CodeView));
        using var pdbStream = embedded ? null : File.OpenRead(Path.ChangeExtension(output, ".pdb"));
        using var pdb = embedded ? pe.ReadEmbeddedPortablePdbDebugDirectoryData(entry) : MetadataReaderProvider.FromPortablePdbStream(pdbStream!);
        var id = Convert.ToHexString(pdb.GetMetadataReader().DebugMetadataHeader!.Id.AsSpan());
        var module = new CoverageModule(id, Path.GetFileName(output), []);
        Assert.True(ModuleIdentity.Matches(output, module));
        Assert.False(ModuleIdentity.Matches(output, module with { Id = new string('0', id.Length) }));
        Assert.False(ModuleIdentity.Matches(output + ".missing", module));
    }
}
