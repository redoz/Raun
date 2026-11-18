using System.Collections.Concurrent;
using Raun.Model;

namespace Raun;

/// <summary>
/// What <see cref="ScenarioRegistry"/> keeps: definition factories by registration key. An instance,
/// so a test can have one of its own; the process has one, behind the static registry.
/// </summary>
internal sealed class ScenarioCatalog
{
    private readonly ConcurrentDictionary<string, Func<ScenarioDefinition>> _factories = new();

    public void Register(string key, Func<ScenarioDefinition> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[key] = factory;
    }

    public bool TryGet(string key, out Func<ScenarioDefinition>? factory)
        => _factories.TryGetValue(key, out factory);

    public IReadOnlyCollection<string> Keys => [.. Snapshot().Select(pair => pair.Key)];

    public IReadOnlyList<ScenarioDefinition> Definitions()
    {
        var entries = Snapshot();
        Array.Sort(entries, static (a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        var definitions = new List<ScenarioDefinition>(entries.Length);
        foreach (var entry in entries)
        {
            definitions.Add(entry.Value());
        }

        return definitions;
    }

    // ConcurrentDictionary.ToArray takes its locks and copies what is there. Enumerating the
    // dictionary through LINQ instead sizes an array first and copies second, and throws when a
    // registration lands in between — which a module initializer can do at any moment.
    private KeyValuePair<string, Func<ScenarioDefinition>>[] Snapshot() => _factories.ToArray();
}
