using Raun.Coverage.MicrosoftXml;

namespace Raun.Coverage;

/// <summary>The single mapping from resolver evidence to the report's four outcomes.</summary>
internal static class CoverageOutcome
{
    public static (string Outcome, string? Reason) Of(EvidenceStatus status) => status switch
    {
        EvidenceStatus.Complete or EvidenceStatus.Exercised => ("covered", null),
        EvidenceStatus.Incomplete or EvidenceStatus.Partial or EvidenceStatus.Untriggered => ("partial", null),
        EvidenceStatus.NotReached => ("uncovered", null),
        EvidenceStatus.Reached => ("unknown", "expression"),
        EvidenceStatus.Evaluated => ("unknown", "filter"),
        EvidenceStatus.SharedRange => ("unknown", "shared-range"),
        EvidenceStatus.Unknown => ("unknown", "unresolved"),
        EvidenceStatus.Unmeasurable => ("unknown", "library"),
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Undefined evidence status."),
    };
}
