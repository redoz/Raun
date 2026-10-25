using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Coverage.Inventory;

namespace Raun.Coverage.MicrosoftXml;

internal static class CoverageResolver
{
    public static EvidenceStatus Resolve(InventoryBranch branch, CoverageObservations observations, CoverageSourceVerification? verification = null)
    {
        if (branch.Measurement == MeasurementKind.Helper)
        {
            return EvidenceStatus.Unmeasurable;
        }
        var modules = observations.Modules.Where(module =>
            string.Equals(Path.GetFileNameWithoutExtension(module.Name), branch.Descriptor.Member.Assembly, StringComparison.Ordinal)
            && module.Ranges.Any(range => SamePath(range.Source.Path, branch.Descriptor.Location.Path))).ToArray();
        if (modules.Length == 0 || modules.Select(module => module.Id).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            return EvidenceStatus.Unknown;
        }
        var ranges = modules.SelectMany(module => module.Ranges)
            .Where(range => SamePath(range.Source.Path, branch.Descriptor.Location.Path)).ToArray();
        if (ranges.Select(range => range.Source).Distinct().Any(source => !(verification?.Verify(branch, source) ?? VerifySource(branch, source))))
        {
            return EvidenceStatus.Unknown;
        }
        var pathHits = Raun.Coverage.Coverlet.CoverletBranchEvidence.PathHits(branch, modules);
        if (pathHits.Length > 1 && branch.Measurement is MeasurementKind.Decision or MeasurementKind.Expression or MeasurementKind.Guard)
        {
            return pathHits.All(hit => hit == 0) ? EvidenceStatus.NotReached
                : pathHits.All(hit => hit > 0) ? EvidenceStatus.Complete : EvidenceStatus.Incomplete;
        }
        var hits = branch.Outcomes.Select(outcome => Match(outcome, branch, ranges)).ToArray();
        if (branch.Measurement == MeasurementKind.Decision && branch.Syntax is IfStatementSyntax conditional
            && !hits.Any(hit => hit is CoverageHit.Yes or CoverageHit.Partial)
            && Match(conditional.Condition.GetLocation().GetLineSpan(), branch, ranges) == CoverageHit.No)
        {
            return EvidenceStatus.NotReached;
        }
        if (branch.Measurement == MeasurementKind.Guard && branch.Syntax is IfStatementSyntax guard && hits.Length == 1 && hits[0] != CoverageHit.Yes)
        {
            return Match(guard.Condition.GetLocation().GetLineSpan(), branch, ranges) switch
            {
                CoverageHit.Yes => EvidenceStatus.Untriggered,
                CoverageHit.No => EvidenceStatus.NotReached,
                _ => EvidenceStatus.Unknown,
            };
        }
        if (hits.Length == 0 || hits.Any(hit => hit is null))
        {
            return EvidenceStatus.Unknown;
        }
        if (branch.Measurement is MeasurementKind.Expression or MeasurementKind.Filter)
        {
            return hits[0] switch
            {
                CoverageHit.Yes => branch.Measurement == MeasurementKind.Filter ? EvidenceStatus.Evaluated : EvidenceStatus.Reached,
                CoverageHit.Partial => SharesRange(branch, ranges) ? EvidenceStatus.SharedRange : EvidenceStatus.Partial,
                _ => EvidenceStatus.NotReached,
            };
        }
        if (hits.Any(hit => hit == CoverageHit.Partial))
        {
            return EvidenceStatus.Unknown;
        }
        if (branch.Measurement is MeasurementKind.Guard or MeasurementKind.Handler)
        {
            return hits[0] == CoverageHit.Yes ? EvidenceStatus.Exercised : EvidenceStatus.NotReached;
        }
        return hits.All(hit => hit == CoverageHit.Yes) ? EvidenceStatus.Complete
            : hits.All(hit => hit == CoverageHit.No) ? EvidenceStatus.NotReached : EvidenceStatus.Incomplete;
    }

    private static CoverageHit? Match(FileLinePositionSpan? outcome, InventoryBranch branch, CoverageRange[] ranges)
        => Match(outcome, branch, ranges, out _);

    private static CoverageHit? Match(FileLinePositionSpan? outcome, InventoryBranch branch, CoverageRange[] ranges, out CoverageRange? matched)
    {
        matched = null;
        if (outcome is not { } span)
        {
            return null;
        }
        var candidates = ranges.Where(range => Contains(range, span)).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }
        var minimum = candidates.Min(Size);
        var matches = candidates.Where(range => Size(range) == minimum).ToArray();
        if (branch.Measurement == MeasurementKind.Decision && branch.Outcomes.Any(other =>
            other is { } sibling && sibling != span && matches.Any(range => Contains(range, sibling))))
        {
            return null;
        }
        if (matches.Select(range => (range.Start, range.End)).Distinct().Count() != 1)
        {
            return null;
        }
        matched = matches[0];
        return matches.Any(range => range.Hit == CoverageHit.Yes) ? CoverageHit.Yes
            : matches.Any(range => range.Hit == CoverageHit.Partial) ? CoverageHit.Partial : CoverageHit.No;
    }

    // A partly executed range that also holds another inventoried branch's test cannot say which of them missed.
    private static bool SharesRange(InventoryBranch branch, CoverageRange[] ranges)
    {
        if (Match(branch.Outcomes[0], branch, ranges, out var range) is null || range is null)
        {
            return false;
        }
        return branch.Syntax.SyntaxTree.GetRoot().DescendantNodesAndSelf().Any(node =>
            node != branch.Syntax && Raun.Coverage.Coverlet.CoverletBranchEvidence.IsBranchNode(node) && Overlaps(range, TestOf(node)));
    }

    // The part of a branch node that executes as its test; a loop or if body is not the test.
    private static FileLinePositionSpan TestOf(SyntaxNode node) => (node switch
    {
        IfStatementSyntax statement => statement.Condition,
        WhileStatementSyntax statement => statement.Condition,
        DoStatementSyntax statement => statement.Condition,
        ForStatementSyntax statement => (SyntaxNode?)statement.Condition ?? statement,
        CommonForEachStatementSyntax statement => statement.Expression,
        SwitchStatementSyntax statement => statement.Expression,
        SwitchExpressionSyntax expression => expression.GoverningExpression,
        ConditionalExpressionSyntax expression => expression.Condition,
        CatchFilterClauseSyntax filter => filter.FilterExpression,
        _ => node,
    }).GetLocation().GetLineSpan();

    private static bool Overlaps(CoverageRange range, FileLinePositionSpan span)
        => range.Start.CompareTo(span.EndLinePosition) < 0 && range.End.CompareTo(span.StartLinePosition) > 0;

    private static bool Contains(CoverageRange range, FileLinePositionSpan span)
        => range.Start.CompareTo(span.StartLinePosition) <= 0 && range.End.CompareTo(span.EndLinePosition) >= 0;

    private static long Size(CoverageRange range)
        => ((long)range.End.Line - range.Start.Line) * 1_000_000 + range.End.Character - range.Start.Character;

    private static bool SamePath(string coveragePath, string sourcePath)
        => string.Equals(Normalize(coveragePath), Normalize(sourcePath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Normalize(string path)
        => Path.GetFullPath(path.Replace(@"\\", @"\", StringComparison.Ordinal));

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "SHA1 is a compiler document checksum, not a security primitive.")]
    internal static bool VerifySource(InventoryBranch branch, CoverageSource source)
    {
        var text = branch.Syntax.SyntaxTree.GetText();
        byte[] bytes;
        if (File.Exists(branch.Descriptor.Location.Path))
        {
            if (!string.Equals(File.ReadAllText(branch.Descriptor.Location.Path), text.ToString(), StringComparison.Ordinal))
            {
                return false;
            }
            bytes = File.ReadAllBytes(branch.Descriptor.Location.Path);
        }
        else
        {
            bytes = (text.Encoding ?? Encoding.UTF8).GetBytes(text.ToString());
        }
        return VerifyBytes(source, bytes);
    }

    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "SHA1 is a compiler document checksum, not a security primitive.")]
    internal static bool VerifyBytes(CoverageSource source, byte[] bytes)
    {
        var hash = source.ChecksumType.ToUpperInvariant() switch
        {
            "SHA256" => SHA256.HashData(bytes),
            "SHA1" => SHA1.HashData(bytes),
            _ => [],
        };
        return hash.Length > 0 && string.Equals(Convert.ToHexString(hash), source.Checksum, StringComparison.OrdinalIgnoreCase);
    }
}
