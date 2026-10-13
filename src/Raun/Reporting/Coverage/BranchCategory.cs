namespace Raun.Reporting.Coverage;

/// <summary>The purpose of a source branch, independent of its execution evidence.</summary>
public enum BranchCategory
{
    ArgumentCheck,
    ErrorHandling,
    NullPropagation,
    Logging,
    Logic,
}
