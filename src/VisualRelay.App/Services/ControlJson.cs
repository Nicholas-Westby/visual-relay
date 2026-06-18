using System.Text.Json;

namespace VisualRelay.App.Services;

/// <summary>
/// Tiny JSON helpers for the control API: serializes small response objects
/// with <see cref="System.Text.Json"/> and reads single scalar fields out of an
/// optional request body without throwing on malformed/absent input.
/// </summary>
internal static class Json
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    /// <summary>Serializes an ordered set of name/value pairs into a JSON object string.</summary>
    public static string Object(params (string Name, object? Value)[] fields)
    {
        var map = new Dictionary<string, object?>(fields.Length, StringComparer.Ordinal);
        foreach (var (name, value) in fields)
        {
            map[name] = value;
        }

        return JsonSerializer.Serialize(map, Options);
    }

    /// <summary>Serializes any object graph (used for the /state snapshot).</summary>
    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Reads a string field from a JSON request body. Returns null when the body
    /// is empty, not an object, malformed, or the field is missing/not a string.
    /// </summary>
    public static string? ReadString(string? body, string field)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(field, out var prop)
                && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a boolean field from a JSON request body. Returns null when the body
    /// is empty, not an object, malformed, or the field is missing/not a bool.
    /// </summary>
    public static bool? ReadBool(string? body, string field)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(field, out var prop)
                && prop.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return prop.GetBoolean();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
