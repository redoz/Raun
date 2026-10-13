using Raun.Coverage.Coverlet;
using System.Reflection;
using Raun.Coverage.Classification;
using Raun.Coverage.Inventory;
using Raun.Coverage.MicrosoftXml;
using Raun.Coverage.Projects;
using Raun.Reporting.Coverage;

namespace Raun.Coverage;

/// <summary>Analyzes source projects and Microsoft XML or Coverlet JSON without a test-platform dependency.</summary>
public static class ClassifiedCoverageAnalyzer
{
    /// <summary>Registers the solution's SDK before a host can load competing MSBuild assemblies.</summary>
    public static void Prepare(string invocationDirectory)
    {
        if ((SolutionLocator.Resolve(invocationDirectory) ?? SolutionLocator.Resolve(Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? invocationDirectory)) is { } solution)
        {
            WorkspaceSdk.EnsureRegistered(Path.GetDirectoryName(solution)!);
        }
    }

    public static async Task<ReportCoverage> AnalyzeAsync(
        string xmlPath, string invocationDirectory, CollectorScope scope,
        IReadOnlyList<Func<IBranchClassificationMiddleware>> middlewareFactories, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(xmlPath);
        var observations = string.Equals(Path.GetExtension(xmlPath), ".json", StringComparison.OrdinalIgnoreCase)
            ? CoverletCoverageJsonReader.Read(stream) : MicrosoftCoverageXmlReader.Read(stream);
        var solution = SolutionLocator.Resolve(invocationDirectory) ?? ResolveSourceHints(observations);
        if (solution is null)
        {
            return new ReportCoverage { Status = "unavailable", Diagnostics = ["No source solution was found."] };
        }
        using var loaded = await SourceProjectLoader.LoadAsync(solution, scope, cancellationToken, invocationDirectory: invocationDirectory).ConfigureAwait(false);
        var pipeline = BranchClassificationPipeline.Create(middlewareFactories);
        var diagnostics = loaded.Diagnostics.ToList();
        var facts = new List<AnalyzedBranch>();
        var sourceVerification = new CoverageSourceVerification();
        if (observations.Modules.Any(module => module.IsCoverlet))
        {
            var reportedModules = loaded.Projects.Count(project => observations.Modules.Any(module => string.Equals(Path.GetFileNameWithoutExtension(module.Name), project.Compilation.AssemblyName, StringComparison.Ordinal)));
            diagnostics.Add($"Coverlet reported {reportedModules} of {loaded.Projects.Count} eligible source modules; absent modules omitted.");
            diagnostics.Add("Coverlet JSON has no report build identity. Source ranges use the current linked PDB; positive line hits shared by several statements remain partial.");
            observations = new CoverageObservations(observations.Modules.Select(module =>
            {
                var project = loaded.Projects.SingleOrDefault(project => string.Equals(project.Compilation.AssemblyName, Path.GetFileNameWithoutExtension(module.Name), StringComparison.Ordinal));
                return project is null ? module : CoverletPdbMapper.Map(module, project.OutputPath) with { HasFrequencyCounts = !scope.CoverletSingleHit };
            }).ToArray());
        }
        foreach (var project in loaded.Projects.Where(project => !observations.Modules.Any(module => module.IsCoverlet)
            || observations.Modules.Any(module => string.Equals(Path.GetFileNameWithoutExtension(module.Name), project.Compilation.AssemblyName, StringComparison.Ordinal))))
        {
            if (!observations.Modules.Any(module => string.Equals(Path.GetFileNameWithoutExtension(module.Name), project.Compilation.AssemblyName, StringComparison.Ordinal)))
            {
                diagnostics.Add(project.Name + ": no execution evidence; source branches remain in inventory with unknown outcomes");
            }
            var matchingModules = observations.Modules.Where(module => ModuleIdentity.Matches(project.OutputPath, module)
                && string.Equals(Path.GetFileNameWithoutExtension(module.Name), project.Compilation.AssemblyName, StringComparison.Ordinal)).ToArray();
            var verifiedObservations = new CoverageObservations(matchingModules);
            if (matchingModules.Length == 0 && observations.Modules.Any(module => string.Equals(Path.GetFileNameWithoutExtension(module.Name), project.Compilation.AssemblyName, StringComparison.Ordinal)))
            {
                diagnostics.Add(project.Name + ": build/PDB identity does not match; outcomes remain unverified");
            }
            foreach (var branch in BranchInventoryBuilder.Build(project.Compilation, scope, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var category = pipeline.Classify(branch.Context);
                diagnostics.AddRange(category.Diagnostics);
                facts.Add(new AnalyzedBranch(branch.Descriptor.Member, category.Category, branch.Measurement,
                    branch.Measurement == MeasurementKind.Helper ? EvidenceStatus.Unmeasurable
                        : project.IsVerified ? CoverageResolver.Resolve(branch, verifiedObservations, sourceVerification) : EvidenceStatus.Unknown) { Source = branch, Observations = project.IsVerified ? verifiedObservations : null });
            }
        }
        var types = facts.GroupBy(branch => (branch.Member.Assembly, branch.Member.Type))
            .OrderBy(group => group.Key.Assembly, StringComparer.Ordinal).ThenBy(group => group.Key.Type, StringComparer.Ordinal)
            .Select(group => new ReportCoverageType(group.Key.Assembly, group.Key.Type, Summarize(group),
                group.GroupBy(branch => branch.Member.Signature).OrderBy(method => method.Key, StringComparer.Ordinal)
                    .Select(method => new ReportCoverageMethod(method.Key, Summarize(method)) { Sources = CoverageSourceBuilder.Build(method.Where(fact => fact.Source is not null).Select(fact => (fact.Source!, fact.Category, fact.Status)), method.First().Observations) }).ToArray()) { Namespace = group.First().Member.Namespace }).ToArray();
        return new ReportCoverage { Status = "available", BranchPaths = SummarizeBranchPaths(observations), Categories = Summarize(facts), Types = types, Diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray() };
    }

    internal static ReportCoverageBranchPaths? SummarizeBranchPaths(CoverageObservations observations)
    {
        var modules = observations.Modules.Where(module => module.IsCoverlet).ToArray();
        return modules.Length == 0 ? null : new ReportCoverageBranchPaths(
            modules.Sum(module => module.Paths.Count(path => path.Hits > 0)),
            modules.Sum(module => module.Paths.Count));
    }

    private static string? ResolveSourceHints(CoverageObservations observations)
    {
        var solutions = observations.Modules.SelectMany(module => module.Ranges).Select(range => range.Source).Distinct()
            .Select(source => (Source: source, Path: source.Path.Replace(@"\\", @"\", StringComparison.Ordinal)))
            .Where(item => File.Exists(item.Path) && CoverageResolver.VerifyBytes(item.Source, File.ReadAllBytes(item.Path)))
            .Select(item => SolutionLocator.Resolve(Path.GetDirectoryName(Path.GetFullPath(item.Path))!))
            .Where(path => path is not null).Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        return solutions.Length switch
        {
            0 => null,
            1 => solutions[0],
            _ => throw new InvalidOperationException("Verified coverage documents point to several source solutions."),
        };
    }

    internal static ReportCoverageCategory[] Summarize(IEnumerable<AnalyzedBranch> facts)
        => facts.GroupBy(branch => branch.Category).OrderBy(group => (int?)group.Key ?? int.MaxValue)
            .Select(group => new ReportCoverageCategory(group.Key, new ReportCoverageCounts
            {
                Inventory = group.Count(),
                Exercised = group.Count(branch => branch.Status == EvidenceStatus.Exercised),
                ExerciseMeasurable = group.Count(branch => branch.Measurement is MeasurementKind.Guard or MeasurementKind.Handler
                    && branch.Status is EvidenceStatus.Exercised or EvidenceStatus.NotReached),
                Complete = group.Count(branch => branch.Status == EvidenceStatus.Complete),
                DecisionMeasurable = group.Count(branch => branch.Measurement == MeasurementKind.Decision
                    && branch.Status is EvidenceStatus.Complete or EvidenceStatus.Incomplete or EvidenceStatus.NotReached),
                Incomplete = group.Count(branch => branch.Status == EvidenceStatus.Incomplete),
                Reached = group.Count(branch => branch.Status == EvidenceStatus.Reached),
                Partial = group.Count(branch => branch.Measurement == MeasurementKind.Expression && branch.Status == EvidenceStatus.Partial),
                FilterPartial = group.Count(branch => branch.Measurement == MeasurementKind.Filter && branch.Status == EvidenceStatus.Partial),
                NotReached = group.Count(branch => branch.Status == EvidenceStatus.NotReached),
                ExpressionNotReached = group.Count(branch => branch.Measurement == MeasurementKind.Expression && branch.Status == EvidenceStatus.NotReached),
                FilterNotReached = group.Count(branch => branch.Measurement == MeasurementKind.Filter && branch.Status == EvidenceStatus.NotReached),
                Evaluated = group.Count(branch => branch.Status == EvidenceStatus.Evaluated),
                Unknown = group.Count(branch => branch.Status == EvidenceStatus.Unknown),
                Unmeasurable = group.Count(branch => branch.Status == EvidenceStatus.Unmeasurable),
            })).ToArray();

    internal sealed record AnalyzedBranch(MemberIdentity Member, BranchCategory? Category, MeasurementKind Measurement, EvidenceStatus Status)
    {
        public InventoryBranch? Source { get; init; }
        public CoverageObservations? Observations { get; init; }
    }
}


