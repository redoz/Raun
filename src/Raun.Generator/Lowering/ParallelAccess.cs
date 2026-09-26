using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raun.Generator.Lowering;

/// <summary>
/// One step's declared access to an earlier step's output, through a role-bearing parameter: the
/// local it reads, the role's verb, whether that role mutates, the step's operation, and where the
/// read is written. Two steps that run in parallel conflict when they access the same local and at
/// least one of them mutates it (RAUN013).
/// </summary>
internal readonly record struct ParallelAccess(
    ILocalSymbol Local, string Verb, bool Exclusive, string Operation, SyntaxNode Node)
{
    /// <summary>
    /// The accesses one lowered call makes. Parameter roles come from <c>[Read]/[Edited]/[Deleted]</c>;
    /// a bare parameter named in a producer's <c>References</c>/<c>Consumes</c> carries the shared
    /// Reference/Consume role. Return roles never appear: a step's return is its own output, shared
    /// with no sibling.
    /// </summary>
    public static List<ParallelAccess> Of(
        IMethodSymbol method,
        string operation,
        SeparatedSyntaxList<ArgumentSyntax> written,
        LoweredCall lowering)
    {
        var accesses = new List<ParallelAccess>();
        var lineageVerbs = LineageVerbs(method);

        for (var p = 0; p < method.Parameters.Length; p++)
        {
            var parameter = method.Parameters[p];
            var verb = AttributeReader.ParameterRole(parameter)
                ?? (lineageVerbs.TryGetValue(parameter.Name, out var lineageVerb) ? lineageVerb : null);
            var index = CallArguments.IndexOf(written, parameter.Name, p);
            if (verb is null || index < 0)
            {
                continue;
            }

            // Mirrors LifecycleVerb.ToLockMode in the runtime assembly: Edit/Delete exclude, the rest share.
            var exclusive = verb is "Edit" or "Delete";
            foreach (var read in lowering.Arguments[index].Reads)
            {
                accesses.Add(new ParallelAccess(read.Local, verb, exclusive, operation, read.Node));
            }
        }

        return accesses;
    }

    /// <summary>
    /// Every conflicting pair in one parallel group, each element's accesses in source order: the
    /// later access is the one reported, against the earlier one it collides with.
    /// </summary>
    public static IEnumerable<(ParallelAccess Earlier, ParallelAccess Later)> Conflicts(
        IEnumerable<IReadOnlyList<ParallelAccess>> elements)
    {
        var earlier = new List<ParallelAccess>();
        foreach (var element in elements)
        {
            foreach (var access in element)
            {
                foreach (var prior in earlier)
                {
                    if (SymbolEqualityComparer.Default.Equals(prior.Local, access.Local)
                        && (prior.Exclusive || access.Exclusive))
                    {
                        yield return (prior, access);
                    }
                }
            }

            earlier.AddRange(element);
        }
    }

    /// <summary>Parameter name → Reference/Consume for every parameter a producer on this method names
    /// as a lineage target (return-role producers and <c>[Edited]</c>-parameter producers alike).</summary>
    private static Dictionary<string, string> LineageVerbs(IMethodSymbol method)
    {
        var verbs = new Dictionary<string, string>(StringComparer.Ordinal);

        var returnLineage = AttributeReader.ProducerLineage(method.GetReturnTypeAttributes());
        if (returnLineage.References.IsEmpty && returnLineage.Consumes.IsEmpty)
        {
            returnLineage = AttributeReader.ProducerLineage(method.GetAttributes());
        }

        AddLineage(verbs, returnLineage);
        foreach (var parameter in method.Parameters)
        {
            if (AttributeReader.ParameterRole(parameter) == "Edit")
            {
                AddLineage(verbs, AttributeReader.ProducerLineage(parameter.GetAttributes()));
            }
        }

        return verbs;

        static void AddLineage(
            Dictionary<string, string> verbs,
            (ImmutableArray<string> References, ImmutableArray<string> Consumes) lineage)
        {
            foreach (var target in lineage.References)
            {
                verbs[target] = "Reference";
            }

            foreach (var target in lineage.Consumes)
            {
                verbs[target] = "Consume";
            }
        }
    }
}
