using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Raun.Generator.Lowering;

/// <summary>
/// An array with VALUE equality, for anything the incremental pipeline carries.
/// </summary>
/// <remarks>
/// Roslyn caches a pipeline step when its input equals the previous run's, so every value in the IR
/// has to compare by content. A record's generated <c>Equals</c> compares each field through
/// <c>EqualityComparer&lt;T&gt;.Default</c>, and every list type — <c>List&lt;T&gt;</c>,
/// <c>T[]</c>, even <c>ImmutableArray&lt;T&gt;</c> — compares by reference there, so a record holding
/// one is never equal to its own re-parse and every keystroke in the editor re-emits the whole file.
/// This type is the fix, and it is deliberately an <see cref="IReadOnlyList{T}"/> so the parser and
/// the emitter read it exactly as they read a list.
/// </remarks>
internal readonly struct EquatableArray<T> : IReadOnlyList<T>, IEquatable<EquatableArray<T>>
{
    /// <summary>The empty array; also what <c>default</c> behaves as.</summary>
    public static EquatableArray<T> Empty { get; } = new([]);

    private readonly T[]? _items;

    public EquatableArray(T[] items) => _items = items;

    public static implicit operator EquatableArray<T>(T[] items) => new(items);

    public static implicit operator EquatableArray<T>(List<T> items) => new(items.ToArray());

    public int Count => _items?.Length ?? 0;

    public T this[int index] => _items![index];

    public bool Equals(EquatableArray<T> other)
    {
        var left = _items;
        var right = other._items;
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        var leftCount = left?.Length ?? 0;
        var rightCount = right?.Length ?? 0;
        if (leftCount != rightCount)
        {
            return false;
        }

        for (var i = 0; i < leftCount; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left![i], right![i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        for (var i = 0; i < Count; i++)
        {
            hash = unchecked((hash * 31) + (_items![i]?.GetHashCode() ?? 0));
        }

        return hash;
    }

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return _items![i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Builds an <see cref="EquatableArray{T}"/> from whatever the parser handed over.</summary>
internal static class Equatable
{
    public static EquatableArray<T> Of<T>(IReadOnlyList<T> items)
    {
        if (items is EquatableArray<T> already)
        {
            return already;
        }

        var copy = new T[items.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = items[i];
        }

        return new EquatableArray<T>(copy);
    }
}

/// <summary>
/// A list of syntax nodes stored with value equality (each wrapped in a <see cref="Syn{T}"/>) and
/// read back as the plain nodes, so the parser and the emitter never see the wrapper.
/// </summary>
internal readonly struct SynList<T> : IReadOnlyList<T>
    where T : SyntaxNode
{
    private readonly EquatableArray<Syn<T>> _items;

    public SynList(EquatableArray<Syn<T>> items) => _items = items;

    public static EquatableArray<Syn<T>> Wrap(IReadOnlyList<T> nodes)
    {
        var wrapped = new Syn<T>[nodes.Count];
        for (var i = 0; i < wrapped.Length; i++)
        {
            wrapped[i] = new Syn<T>(nodes[i]);
        }

        return new EquatableArray<Syn<T>>(wrapped);
    }

    public int Count => _items.Count;

    public T this[int index] => _items[index].Node!;

    public IEnumerator<T> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// A syntax node carried through the incremental pipeline with VALUE equality.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SyntaxNode"/> compares by reference, so a record field holding one is never equal to
/// the same node re-parsed — which is what an editor produces on every keystroke. Equality here is
/// <see cref="SyntaxNode.IsEquivalentTo(SyntaxNode, bool)"/> with <c>topLevel: false</c>: the same
/// code, whatever its trivia. The hash is built from the node's token texts, which are trivia-free,
/// so two nodes that compare equal always hash equal.
/// </para>
/// <para>
/// The node itself is still the thing the emitter emits; this wrapper only decides what "changed"
/// means on the way there.
/// </para>
/// </remarks>
internal readonly struct Syn<T> : IEquatable<Syn<T>>
    where T : SyntaxNode
{
    private readonly int _hash;

    public Syn(T? node)
    {
        Node = node;
        var hash = 17;
        if (node is not null)
        {
            foreach (var token in node.DescendantTokens())
            {
                hash = unchecked((hash * 31) + StringComparer.Ordinal.GetHashCode(token.Text));
            }
        }

        _hash = hash;
    }

    /// <summary>The wrapped node, or null when nothing was lowered here.</summary>
    public T? Node { get; }

    public static implicit operator Syn<T>(T? node) => new(node);

    public bool Equals(Syn<T> other)
        => Node is null || other.Node is null
            ? ReferenceEquals(Node, other.Node)
            : _hash == other._hash && Node.IsEquivalentTo(other.Node, topLevel: false);

    public override bool Equals(object? obj) => obj is Syn<T> other && Equals(other);

    public override int GetHashCode() => _hash;

    public static bool operator ==(Syn<T> left, Syn<T> right) => left.Equals(right);

    public static bool operator !=(Syn<T> left, Syn<T> right) => !left.Equals(right);
}
