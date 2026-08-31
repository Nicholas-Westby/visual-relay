using System.Text.Json;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// The small argument readers the command-family tools share. Every failure returns
/// a sentence that names the argument and what was wrong with it, so the model can
/// repair the call rather than repeat it.
/// </summary>
internal static class CommandToolArguments
{
    /// <summary>Reads a required non-empty string argument.</summary>
    /// <param name="arguments">The model's arguments object.</param>
    /// <param name="name">The argument to read.</param>
    /// <returns>Exactly one of the two members is non-null.</returns>
    public static (string? Value, string? Error) GetString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return (null, $"the arguments must be a JSON object with a \"{name}\" property.");

        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return (null, $"\"{name}\" is required.");

        if (element.ValueKind != JsonValueKind.String)
            return (null, $"\"{name}\" must be a string.");

        var value = element.GetString()!;
        return string.IsNullOrWhiteSpace(value)
            ? (null, $"\"{name}\" must not be empty.")
            : (value, null);
    }

    /// <summary>Reads a required non-empty array of strings.</summary>
    /// <param name="arguments">The model's arguments object.</param>
    /// <param name="name">The argument to read.</param>
    /// <returns>Exactly one of the two members is non-null.</returns>
    public static (IReadOnlyList<string>? Value, string? Error) GetStringArray(
        JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
            return (null, $"the arguments must be a JSON object with a \"{name}\" property.");

        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            return (null, $"\"{name}\" is required.");

        if (element.ValueKind != JsonValueKind.Array)
            return (null, $"\"{name}\" must be an array of strings.");

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return (null, $"every entry of \"{name}\" must be a string.");
            values.Add(item.GetString()!);
        }

        return values.Count == 0
            ? (null, $"\"{name}\" must contain at least one entry.")
            : (values, null);
    }
}
