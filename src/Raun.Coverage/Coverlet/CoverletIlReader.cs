using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Raun.Coverage.MicrosoftXml;

namespace Raun.Coverage.Coverlet;

internal sealed record CoverageInstruction(int Offset, int NextOffset, OpCode Code, int Target, string? Call)
{
    public bool IsConditional => Code.FlowControl == FlowControl.Cond_Branch;
}
internal sealed record CoverageMethod(string Name, IReadOnlyList<CoverageRange> Points, IReadOnlyList<CoverageInstruction> Instructions);

internal static class CoverletIlReader
{
    private static readonly Dictionary<short, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!).ToDictionary(code => code.Value);

    public static CoverageMethod[] Read(PEReader pe, MetadataReader pdb, IReadOnlyDictionary<DocumentHandle, CoverageSource> sources)
    {
        var metadata = pe.GetMetadataReader();
        var result = new List<CoverageMethod>();
        foreach (var handle in metadata.MethodDefinitions)
        {
            var definition = metadata.GetMethodDefinition(handle);
            if (definition.RelativeVirtualAddress == 0) { continue; }
            var bytes = pe.GetMethodBody(definition.RelativeVirtualAddress).GetILBytes();
            if (bytes is null) { continue; }
            var rawPoints = pdb.GetMethodDebugInformation(handle).GetSequencePoints().ToArray();
            // Hidden points still delimit the preceding visible point's IL region.
            var points = rawPoints.Select((point, index) => (Point: point, End: index + 1 < rawPoints.Length ? rawPoints[index + 1].Offset : bytes.Length))
                .Where(item => !item.Point.IsHidden && sources.ContainsKey(item.Point.Document))
                .Select(item => new CoverageRange(sources[item.Point.Document], new(item.Point.StartLine - 1, item.Point.StartColumn - 1), new(item.Point.EndLine - 1, item.Point.EndColumn - 1), CoverageHit.Partial)
                { ILStart = item.Point.Offset, ILEnd = item.End }).ToArray();
            if (points.Length == 0) { continue; }
            var instructions = new List<CoverageInstruction>();
            for (var position = 0; position < bytes.Length;)
            {
                var offset = position;
                var value = bytes[position++] == 0xfe ? (short)(0xfe00 | bytes[position++]) : (short)bytes[offset];
                if (!Codes.TryGetValue(value, out var code)) { throw new BadImageFormatException("Unknown IL opcode."); }
                var operand = position;
                var size = code.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + BitConverter.ToInt32(bytes, position) * 4,
                    _ => 4,
                };
                position += size;
                var target = code.OperandType == OperandType.ShortInlineBrTarget ? position + (sbyte)bytes[operand]
                    : code.OperandType == OperandType.InlineBrTarget ? position + BitConverter.ToInt32(bytes, operand) : -1;
                var call = code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj
                    ? CallIdentity(metadata, MetadataTokens.EntityHandle(BitConverter.ToInt32(bytes, operand))) ?? "<unresolved>" : null;
                instructions.Add(new CoverageInstruction(offset, position, code, target, call));
            }
            result.Add(new CoverageMethod(metadata.GetString(definition.Name), points, instructions));
        }
        return result.ToArray();
    }

    private static string? CallIdentity(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.MethodSpecification)
        { return CallIdentity(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method); }
        string name;
        string? owner;
        BlobHandle signature;
        if (handle.Kind == HandleKind.MemberReference)
        {
            var member = reader.GetMemberReference((MemberReferenceHandle)handle);
            name = reader.GetString(member.Name);
            owner = TypeName(reader, member.Parent);
            signature = member.Signature;
        }
        else if (handle.Kind == HandleKind.MethodDefinition)
        {
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
            name = reader.GetString(method.Name);
            owner = TypeName(reader, method.GetDeclaringType());
            signature = method.Signature;
        }
        else { return null; }
        if (owner is null) { return null; }
        var blob = reader.GetBlobReader(signature);
        var header = blob.ReadSignatureHeader();
        if (header.IsGeneric) { blob.ReadCompressedInteger(); }
        var parameters = blob.ReadCompressedInteger();
        return owner + "::" + name + "/" + parameters.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? TypeName(MetadataReader reader, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeReference)
        {
            var type = reader.GetTypeReference((TypeReferenceHandle)handle);
            var parent = type.ResolutionScope.Kind == HandleKind.TypeReference ? TypeName(reader, type.ResolutionScope) + "+" : reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." : "";
            return parent + reader.GetString(type.Name);
        }
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
            var declaring = type.GetDeclaringType();
            var parent = !declaring.IsNil ? TypeName(reader, declaring) + "+" : reader.GetString(type.Namespace) is { Length: > 0 } ns ? ns + "." : "";
            return parent + reader.GetString(type.Name);
        }
        if (handle.Kind == HandleKind.TypeSpecification)
        {
            var signature = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
            if (signature.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
            {
                signature.ReadSignatureTypeCode();
                return TypeName(reader, signature.ReadTypeHandle());
            }
        }
        return null;
    }
}
