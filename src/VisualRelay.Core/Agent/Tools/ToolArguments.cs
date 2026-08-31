using System.Globalization;
using System.Text.Json;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// Reads the model's arguments leniently. Models routinely send a number as a
/// string, a single string where an array is declared, or omit an optional
/// argument entirely; none of those is worth spending a turn on an error the
/// model can only fix by guessing, so each is coerced here instead.
/// </summary>
internal static class ToolArguments
{
    /// <summary>Reads a string argument.</summary>
    /// <param name="arguments">The arguments object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or null when absent or not a string.</returns>
    internal static string? String(JsonElement arguments, string name) =>
        TryGet(arguments, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads an integer argument, tolerating a numeric string.</summary>
    /// <param name="arguments">The arguments object.</param>
    /// <param name="name">The property name.</param>
    /// <param name="fallback">Used when the argument is absent or unreadable.</param>
    /// <returns>The value or the fallback.</returns>
    internal static int Int(JsonElement arguments, string name, int fallback)
    {
        if (!TryGet(arguments, name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => fallback,
        };
    }

    /// <summary>Reads a boolean argument, tolerating "true"/"false" as strings.</summary>
    /// <param name="arguments">The arguments object.</param>
    /// <param name="name">The property name.</param>
    /// <param name="fallback">Used when the argument is absent or unreadable.</param>
    /// <returns>The value or the fallback.</returns>
    internal static bool Bool(JsonElement arguments, string name, bool fallback)
    {
        if (!TryGet(arguments, name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback,
        };
    }

    /// <summary>
    /// Reads an array of strings, accepting a bare string as a one-element array.
    /// </summary>
    /// <param name="arguments">The arguments object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The values, empty when the argument is absent.</returns>
    internal static IReadOnlyList<string> StringArray(JsonElement arguments, string name)
    {
        if (!TryGet(arguments, name, out var value)) return [];
        if (value.ValueKind == JsonValueKind.String)
        {
            var single = value.GetString();
            return single is null ? [] : [single];
        }

        if (value.ValueKind != JsonValueKind.Array) return [];

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) continue;
            var item = element.GetString();
            if (!string.IsNullOrWhiteSpace(item)) items.Add(item);
        }

        return items;
    }

    private static bool TryGet(JsonElement arguments, string name, out JsonElement value)
    {
        value = default;
        return arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(name, out value)
            && value.ValueKind != JsonValueKind.Null;
    }
}
