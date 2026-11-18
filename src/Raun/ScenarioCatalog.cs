using System.Collections.Concurrent;
using System.Globalization;
using Raun.Model;

namespace Raun;

/// <summary>
/// What <see cref="ScenarioRegistry"/> keeps: definition factories by registration key, and the
/// registrations it refused. An instance, so a test can have one of its own; the process has one,
/// behind the static registry.
/// </summary>
internal sealed class ScenarioCatalog(int contractVersion = ScenarioRegistry.ContractVersion)
{
    private readonly ConcurrentDictionary<string, Func<ScenarioDefinition>> _factories = new();
    private readonly ConcurrentQueue<(string Assembly, int GeneratedFor)> _refused = new();

    /// <summary>A registration made by hand: it states no contract, because nothing generated
    /// stands between the definition and this runtime.</summary>
    public void Register(string key, Func<ScenarioDefinition> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[key] = factory;
    }

    /// <summary>A registration made by generated code, stating the contract it was generated for.
    /// For any other contract than this runtime's it is refused: the factory is not kept, never
    /// called, and <see cref="Definitions"/> fails until the code is regenerated.</summary>
    public void Register(int generatedFor, string key, Func<ScenarioDefinition> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        if (generatedFor != contractVersion)
        {
            _refused.Enqueue((factory.Method.Module.Assembly.GetName().Name ?? "?", generatedFor));
            return;
        }

        _factories[key] = factory;
    }

    public bool TryGet(string key, out Func<ScenarioDefinition>? factory)
        => _factories.TryGetValue(key, out factory);

    public IReadOnlyCollection<string> Keys => [.. Snapshot().Select(pair => pair.Key)];

    public IReadOnlyList<ScenarioDefinition> Definitions()
    {
        if (!_refused.IsEmpty)
        {
            throw new ScenarioContractException(Refusal());
        }

        var entries = Snapshot();
        Array.Sort(entries, static (a, b) => StringComparer.Ordinal.Compare(a.Key, b.Key));
        var definitions = new List<ScenarioDefinition>(entries.Length);
        foreach (var entry in entries)
        {
            definitions.Add(entry.Value());
        }

        return definitions;
    }

    private string Refusal()
    {
        var groups = _refused.ToArray()
            .GroupBy(refused => refused)
            .OrderBy(group => group.Key.Assembly, StringComparer.Ordinal)
            .ThenBy(group => group.Key.GeneratedFor)
            .Select(group => string.Create(
                CultureInfo.InvariantCulture,
                $"'{group.Key.Assembly}' (contract {group.Key.GeneratedFor}, {group.Count()} {(group.Count() == 1 ? "scenario" : "scenarios")})"));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Scenarios were generated for a different Raun and were not registered: {string.Join(", ", groups)}; "
            + $"this Raun is contract {contractVersion}. Rebuild those projects against the Raun version the test application uses.");
    }

    // ConcurrentDictionary.ToArray takes its locks and copies what is there. Enumerating the
    // dictionary through LINQ instead sizes an array first and copies second, and throws when a
    // registration lands in between — which a module initializer can do at any moment.
    private KeyValuePair<string, Func<ScenarioDefinition>>[] Snapshot() => _factories.ToArray();
}
