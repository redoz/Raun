namespace Raun.Reporting.Coverage;

/// <summary>Platform-neutral classified coverage data embedded in a run report.</summary>
public sealed record ReportCoverage
{
    public string Status { get; init; } = "pending";
    public ReportCoverageBranchPaths? BranchPaths { get; init; }
    public IReadOnlyList<ReportCoverageCategory> Categories { get; init; } = [];
    public IReadOnlyList<ReportCoverageType> Types { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

/// <summary>Collector-reported branch paths, independent of the classified source inventory.</summary>
public sealed record ReportCoverageBranchPaths(int Covered, int Total);

public sealed record ReportCoverageType(
    string Assembly, string Name, IReadOnlyList<ReportCoverageCategory> Categories, IReadOnlyList<ReportCoverageMethod> Methods)
{
    public string? Namespace { get; init; }
}

public sealed record ReportCoverageMethod(string Signature, IReadOnlyList<ReportCoverageCategory> Categories)
{
    public IReadOnlyList<ReportCoverageSource> Sources { get; init; } = [];
}

public sealed record ReportCoverageSource(string Path, int StartLine, string Text, IReadOnlyList<ReportCoverageToken> Tokens, IReadOnlyList<ReportCoverageBranch> Branches)
{
    public IReadOnlyList<int> NotExecutedLines { get; init; } = [];
    public IReadOnlyList<ReportCoverageExecution> Execution { get; init; } = [];
}
public sealed record ReportCoverageExecution(int Start, int Length, string State);
public sealed record ReportCoverageToken(int Start, int Length, string Kind);
/// <summary>One classified branch inside a source snippet.</summary>
/// <param name="Start">Offset of the branch in the snippet text.</param>
/// <param name="Length">Length of the branch in the snippet text.</param>
/// <param name="Category">Classification category, or null when the branch is unclassified.</param>
/// <param name="Outcome">
/// <c>covered</c> (every path shown to run), <c>partial</c> (some path shown not to run), <c>uncovered</c> (shown never
/// to run) or <c>unknown</c> (the evidence cannot tell; see <see cref="Reason"/>).
/// </param>
public sealed record ReportCoverageBranch(int Start, int Length, BranchCategory? Category, string Outcome)
{
    /// <summary>
    /// What the evidence says about the outcome; null when the outcome needs no explanation. For <c>unknown</c>: one
    /// of <c>expression</c> (an expression ran but not which path), <c>filter</c> (an exception filter ran but not which
    /// outcome), <c>shared-range</c> (partial execution of a range several branches share), <c>unresolved</c> (no
    /// evidence ties this branch to its paths) or <c>library</c> (the check runs inside a .NET library). For
    /// <c>partial</c>: <c>untriggered</c> (a check's condition ran but its throw never did) or null.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Observed path counters; their order does not imply true/false arm identity.</summary>
    public IReadOnlyList<long> PathHits { get; init; } = [];
}

public sealed record ReportCoverageCategory(BranchCategory? Category, ReportCoverageCounts Counts);

/// <summary>Outcome counters; measurable = covered + partial + uncovered.</summary>
public sealed record ReportCoverageCounts
{
    /// <summary>Branches found in source, whatever their outcome.</summary>
    public int Inventory { get; init; }

    /// <summary>Branches with outcome <c>covered</c>.</summary>
    public int Covered { get; init; }

    /// <summary>Branches with outcome <c>partial</c>.</summary>
    public int Partial { get; init; }

    /// <summary>Branches with outcome <c>uncovered</c>.</summary>
    public int Uncovered { get; init; }

    /// <summary>Branches with outcome <c>unknown</c>; not part of the measurable total.</summary>
    public int Unknown { get; init; }
}
