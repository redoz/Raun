using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Raun.Coverage.Inventory;
using Raun.Coverage.MicrosoftXml;
using Raun.Reporting.Coverage;

namespace Raun.Coverage;

internal static class CoverageSourceBuilder
{
    public static ReportCoverageSource[] Build(IEnumerable<(InventoryBranch Branch, BranchCategory? Category, EvidenceStatus Status)> facts, CoverageObservations? observations = null)
    {
        var result = new List<ReportCoverageSource>();
        foreach (var group in facts.GroupBy(fact => fact.Branch.Syntax.SyntaxTree))
        {
            var tree = group.Key;
            var source = tree.GetText();
            var evidence = VerifiedRanges(group.First().Branch, observations);
            if (observations?.Modules.Any(module => module.IsCoverlet) == true)
            {
                evidence = evidence.Select(item => item.Hit == CoverageHit.Yes && group.Any(fact =>
                    fact.Branch.Measurement == MeasurementKind.Expression
                    && item.Start.CompareTo(fact.Branch.Descriptor.Location.StartLinePosition) <= 0
                    && item.End.CompareTo(fact.Branch.Descriptor.Location.EndLinePosition) >= 0)
                    ? item with { Hit = CoverageHit.Partial } : item).ToArray();
                evidence = [.. evidence, .. Raun.Coverage.Coverlet.CoverletConditionalExecution.Build(group.Select(fact => fact.Branch), observations.Modules)];
            }
            var ranges = group.Select(fact =>
            {
                var branchSpan = fact.Branch.Syntax.Span;
                var startLine = source.Lines.GetLineFromPosition(branchSpan.Start).LineNumber;
                var endLine = source.Lines.GetLineFromPosition(Math.Max(branchSpan.Start, branchSpan.End - 1)).LineNumber;
                return (Start: Math.Max(0, startLine - 2), End: Math.Min(source.Lines.Count - 1, endLine + 2));
            }).Distinct().OrderBy(range => range.Start).ThenBy(range => range.End).ToArray();
            var merged = new List<(int Start, int End)>();
            foreach (var range in ranges)
            {
                if (merged.Count > 0 && range.Start <= merged[^1].End + 1)
                {
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, range.End));
                }
                else { merged.Add(range); }
            }
            foreach (var range in merged)
            {
                var span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(source.Lines[range.Start].Start, source.Lines[range.End].End);
                var tokens = new List<ReportCoverageToken>();
                foreach (var token in tree.GetRoot().DescendantTokens(span))
                {
                    var kind = SyntaxFacts.IsKeywordKind(token.Kind()) ? "keyword"
                        : token.IsKind(SyntaxKind.NumericLiteralToken) ? "number"
                        : token.Kind().ToString().Contains("String", StringComparison.Ordinal) || token.IsKind(SyntaxKind.CharacterLiteralToken) ? "string" : null;
                    if (kind is not null) { AddToken(token.Span, kind); }
                    foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
                    {
                        if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                            || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                        { AddToken(trivia.Span, "comment"); }
                    }
                }
                var branches = group.Where(fact => span.Contains(fact.Branch.Syntax.SpanStart)).Select(fact =>
                    new ReportCoverageBranch(fact.Branch.Syntax.SpanStart - span.Start,
                        Math.Min(fact.Branch.Syntax.Span.End, span.End) - fact.Branch.Syntax.SpanStart, fact.Category, Outcome(fact.Status))
                    {
                        PathHits = evidence.Length == 0 ? [] : Raun.Coverage.Coverlet.CoverletBranchEvidence.PathHits(fact.Branch, observations?.Modules.Where(module => module.HasFrequencyCounts).ToArray() ?? []),
                    }).ToArray();
                result.Add(new ReportCoverageSource(tree.FilePath, range.Start + 1, source.ToString(span), tokens.OrderBy(token => token.Start).ToArray(), branches)
                {
                    Execution = evidence.Where(item => item.Start.Line >= 0 && item.End.Line < source.Lines.Count
                        && item.Start.Character <= source.Lines[item.Start.Line].Span.Length && item.End.Character <= source.Lines[item.End.Line].Span.Length)
                        .Select(item => (Item: item, Start: Math.Max(span.Start, source.Lines.GetPosition(item.Start)), End: Math.Min(span.End, source.Lines.GetPosition(item.End))))
                        .Where(item => item.End > item.Start).Select(item => new ReportCoverageExecution(item.Start - span.Start, item.End - item.Start,
                            item.Item.Hit == CoverageHit.Yes ? "executed" : item.Item.Hit == CoverageHit.No ? "missed" : "partial"))
                        .OrderBy(item => item.Start).ToArray(),
                    NotExecutedLines = Enumerable.Range(range.Start, range.End - range.Start + 1).Where(line =>
                    {
                        var hits = evidence.Where(item => item.Start.Line <= line && (item.End.Line > line || item.End.Line == line && item.End.Character > 0)).ToArray();
                        return hits.Length > 0 && hits.All(item => item.Hit == CoverageHit.No);
                    }).Select(line => line + 1).ToArray(),
                });
                void AddToken(Microsoft.CodeAnalysis.Text.TextSpan tokenSpan, string kind)
                {
                    var start = Math.Max(span.Start, tokenSpan.Start);
                    var end = Math.Min(span.End, tokenSpan.End);
                    if (end > start) { tokens.Add(new ReportCoverageToken(start - span.Start, end - start, kind)); }
                }
            }
        }
        return result.ToArray();
    }
    private static CoverageRange[] VerifiedRanges(InventoryBranch branch, CoverageObservations? observations)
    {
        var modules = observations?.Modules.Where(module => string.Equals(Path.GetFileNameWithoutExtension(module.Name), branch.Descriptor.Member.Assembly, StringComparison.Ordinal)).ToArray() ?? [];
        if (modules.Length == 0 || modules.Select(module => module.Id).Distinct(StringComparer.Ordinal).Count() != 1) { return []; }
        var ranges = modules.SelectMany(module => module.Ranges).Where(range => string.Equals(
            Path.GetFullPath(range.Source.Path.Replace(@"\\", @"\", StringComparison.Ordinal)), Path.GetFullPath(branch.Syntax.SyntaxTree.FilePath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
        if (!ranges.Select(range => range.Source).Distinct().All(document => CoverageResolver.VerifySource(branch, document))) { return []; }
        return ranges.GroupBy(range => (range.Source, range.Start, range.End)).Select(group => group.First() with
        {
            Hit = group.Any(range => range.Hit == CoverageHit.Yes) ? CoverageHit.Yes
                : group.Any(range => range.Hit == CoverageHit.Partial) ? CoverageHit.Partial : CoverageHit.No,
        }).ToArray();
    }
    private static string Outcome(EvidenceStatus status) => status switch
    {
        EvidenceStatus.Complete => "complete",
        EvidenceStatus.Exercised => "exercise",
        EvidenceStatus.Incomplete or EvidenceStatus.Partial => "partial",
        EvidenceStatus.Reached or EvidenceStatus.Evaluated => "reached",
        EvidenceStatus.NotReached => "missed",
        EvidenceStatus.Unmeasurable => "helper",
        _ => "unknown",
    };
}






