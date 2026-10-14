using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Raun;

internal static class ResourcePresentation
{
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IncludeFields = true,
        ReferenceHandler = ReferenceHandler.Preserve,
    };

    internal static string Name(Type type)
        => type.GetCustomAttribute<DisplayNameAttribute>(inherit: true)?.DisplayName is { Length: > 0 } name
            ? name : type.Name;

    internal static string Display(ResourceIdentity resource) => $"{resource.Kind.DisplayName}:{resource.Key}";

    internal static string Data(object value)
    {
        var text = value.ToString() ?? string.Empty;
        var type = value.GetType();
        foreach (var name in new[] { type.Name, Name(type) })
        {
            if (text.StartsWith(name + " {", StringComparison.Ordinal))
            {
                return text[(name.Length + 1)..];
            }
        }
        return text;
    }

    internal static string? Capture(object? value)
    {
        if (value is null or ResourceIdentity) { return null; }
        try { return JsonSerializer.Serialize(value, value.GetType(), SnapshotOptions); }
        catch (Exception)
        {
            // Report evidence must not fail a scenario when a resource cannot be serialized.
            return null;
        }
    }
}
