using System.Diagnostics.CodeAnalysis;
using Raun.Reporting.Coverage;

namespace Raun.Coverage.Classification;

internal sealed record BranchClassificationResult(BranchCategory? Category, IReadOnlyList<string> Diagnostics);

internal sealed class BranchClassificationPipeline(IBranchClassificationMiddleware[] middleware, string? configurationError)
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "User middleware failures are reported as unclassified and must not fail a test run.")]
    public static BranchClassificationPipeline Create(IReadOnlyList<Func<IBranchClassificationMiddleware>> factories)
    {
        try
        {
            return new BranchClassificationPipeline(factories.Select(factory =>
                factory?.Invoke() ?? throw new InvalidOperationException("A classification middleware factory returned null.")).ToArray(), null);
        }
        catch (Exception error)
        {
            return new BranchClassificationPipeline([], error.Message);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "User middleware failures preserve branch inventory and execution facts.")]
    public BranchClassificationResult Classify(BranchClassificationContext context)
    {
        if (configurationError is not null)
        {
            return new(null, [configurationError]);
        }
        try
        {
            return new(Invoke(0, context), []);
        }
        catch (Exception error)
        {
            return new(null, [context.Descriptor.Id + ": " + error.Message]);
        }
    }

    private BranchCategory Invoke(int index, BranchClassificationContext context)
    {
        if (index == middleware.Length)
        {
            return SyntaxBranchClassifier.Classify(context);
        }
        var active = true;
        var called = false;
        try
        {
            var category = middleware[index].Classify(context, () =>
            {
                if (!active || called)
                {
                    throw new InvalidOperationException("Classification next must be called at most once during its middleware invocation.");
                }
                called = true;
                return Invoke(index + 1, context);
            });
            if (!Enum.IsDefined(category))
            {
                throw new InvalidOperationException("Classification middleware returned an invalid category.");
            }
            return category;
        }
        finally
        {
            active = false;
        }
    }
}
