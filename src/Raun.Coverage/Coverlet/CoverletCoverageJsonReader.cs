using System.Text.Json;
using Raun.Coverage.MicrosoftXml;

namespace Raun.Coverage.Coverlet;

internal static class CoverletCoverageJsonReader
{
    public static CoverageObservations Read(Stream stream)
    {
        using var json = JsonDocument.Parse(stream);
        var modules = new List<CoverageModule>();
        if (json.RootElement.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("Expected Coverlet JSON modules."); }
        foreach (var module in json.RootElement.EnumerateObject())
        {
            if (!module.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || module.Value.ValueKind != JsonValueKind.Object)
            { throw new InvalidDataException("Expected Coverlet JSON modules."); }
            var lines = new List<CoverageLine>();
            var paths = new List<CoveragePath>();
            foreach (var document in module.Value.EnumerateObject())
            {
                foreach (var type in document.Value.EnumerateObject())
                {
                    foreach (var method in type.Value.EnumerateObject())
                    {
                        foreach (var line in method.Value.GetProperty("Lines").EnumerateObject())
                        {
                            if (!int.TryParse(line.Name, out var number) || number <= 0 || line.Value.GetInt64() < 0)
                            { throw new InvalidDataException("Invalid Coverlet line counter."); }
                            lines.Add(new CoverageLine(document.Name, number, line.Value.GetInt64()));
                        }
                        foreach (var path in method.Value.GetProperty("Branches").EnumerateArray())
                        {
                            var hits = path.GetProperty("Hits").GetInt64();
                            var line = path.GetProperty("Line").GetInt32();
                            if (hits < 0 || line <= 0) { throw new InvalidDataException("Invalid Coverlet branch counter."); }
                            paths.Add(new CoveragePath(document.Name, method.Name, line, path.GetProperty("Offset").GetInt32(),
                                path.GetProperty("Path").GetInt32(), hits) { EndOffset = path.GetProperty("EndOffset").GetInt32() });
                        }
                    }
                }
            }
            modules.Add(new CoverageModule("", module.Name, []) { IsCoverlet = true, Lines = lines, Paths = paths });
        }
        return new CoverageObservations(modules);
    }
}
