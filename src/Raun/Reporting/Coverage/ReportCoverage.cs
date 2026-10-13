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
public sealed record ReportCoverageBranch(int Start, int Length, BranchCategory? Category, string Outcome)
{
    /// <summary>Observed path counters; their order does not imply true/false arm identity.</summary>
    public IReadOnlyList<long> PathHits { get; init; } = [];
}

public sealed record ReportCoverageCategory(BranchCategory? Category, ReportCoverageCounts Counts);

/// <summary>Integer counters. Categories group structural measurements without redefining them.</summary>
public sealed record ReportCoverageCounts
{
    public int Inventory { get; init; }
    public int Exercised { get; init; }
    public int ExerciseMeasurable { get; init; }
    public int Complete { get; init; }
    public int DecisionMeasurable { get; init; }
    public int Incomplete { get; init; }
    public int Reached { get; init; }
    public int Partial { get; init; }
    public int NotReached { get; init; }
    public int ExpressionNotReached { get; init; }
    public int FilterPartial { get; init; }
    public int FilterNotReached { get; init; }
    public int Evaluated { get; init; }
    public int Unknown { get; init; }
    public int Unmeasurable { get; init; }
}



