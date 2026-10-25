using Microsoft.CodeAnalysis.Text;

namespace Raun.Coverage.MicrosoftXml;

internal enum CoverageHit { No, Partial, Yes }
internal enum EvidenceStatus { Complete, Incomplete, Exercised, Reached, Partial, NotReached, Unknown, Unmeasurable, Evaluated, Untriggered, SharedRange }

internal sealed record CoverageSource(string Path, string ChecksumType, string Checksum);
internal sealed record CoverageRange(CoverageSource Source, LinePosition Start, LinePosition End, CoverageHit Hit)
{
    public int ILStart { get; init; } = -1;
    public int ILEnd { get; init; } = -1;
}
internal sealed record CoverageLine(string Path, int Line, long Hits);
internal sealed record CoveragePath(string SourcePath, string Method, int Line, int Offset, int Path, long Hits)
{
    public int EndOffset { get; init; } = -1;
}
internal sealed record CoverageModule(string Id, string Name, IReadOnlyList<CoverageRange> Ranges)
{
    public IReadOnlyList<Raun.Coverage.Coverlet.CoverageMethod> Methods { get; init; } = [];
    public bool HasFrequencyCounts { get; init; } = true;
    public bool IsCoverlet { get; init; }
    public IReadOnlyList<CoverageLine> Lines { get; init; } = [];
    public IReadOnlyList<CoveragePath> Paths { get; init; } = [];
}
internal sealed record CoverageObservations(IReadOnlyList<CoverageModule> Modules);
