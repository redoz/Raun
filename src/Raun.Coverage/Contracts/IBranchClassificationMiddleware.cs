using Raun.Reporting.Coverage;

namespace Raun.Coverage;

/// <summary>Continues classification of the same branch. Call at most once, synchronously.</summary>
public delegate BranchCategory BranchClassificationNext();

/// <summary>Classifies a source branch, or delegates to later rules. Contains no coverage logic.</summary>
public interface IBranchClassificationMiddleware
{
    BranchCategory Classify(BranchClassificationContext context, BranchClassificationNext next);
}
