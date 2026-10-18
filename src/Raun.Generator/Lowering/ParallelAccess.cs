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
    ILocalSymbol Local, string Effect, bool Exclusive, string Operation, SyntaxNode Node)
{
    /// <summary>
    /// The accesses one lowered call makes. Parameter roles come from <c>[Read]/[Updated]/[Deleted]</c>;
    /// links grant no access. Return roles never appear: a step's return is its own output, shared
    /// with no sibling.
    /// </summary>
    public static List<ParallelAccess> Of(
        IMethodSymbol method,
        string operation,
        SeparatedSyntaxList<ArgumentSyntax> written,
        LoweredCall lowering)
    {
        var accesses = new List<ParallelAccess>();

        for (var p = 0; p < method.Parameters.Length; p++)
        {
            var parameter = method.Parameters[p];
            var verb = AttributeReader.ParameterRole(parameter);
            var index = CallArguments.IndexOf(written, parameter.Name, p);
            if (verb is null || index < 0)
            {
                continue;
            }

            // Mirrors ResourceEffect.ToLockMode in the runtime assembly: Create/Update/Delete exclude, the rest share.
            var exclusive = verb is "Create" or "Update" or "Delete";
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

}
