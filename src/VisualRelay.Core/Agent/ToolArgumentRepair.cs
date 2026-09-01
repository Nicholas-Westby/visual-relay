using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent;

/// <summary>The outcome of trying to make a model's arguments usable.</summary>
/// <param name="Arguments">The repaired arguments, or <c>null</c> when unusable.</param>
/// <param name="Repair">
/// What was done, for the trace and for the model. <c>null</c> when the
/// arguments arrived already valid and nothing was touched.
/// </param>
public sealed record ToolArgumentRepairResult(JsonNode? Arguments, string? Repair)
{
    /// <summary>True when usable arguments came out.</summary>
    public bool Succeeded => Arguments is not null;
}

/// <summary>
/// Repairs the two argument failures worth repairing: JSON truncated mid-emission,
/// and a value of the wrong primitive type. Both are recoverable without guessing
/// at intent, which is the line this class does not cross — a missing required
/// property is reported back to the model rather than invented.
/// </summary>
public static class ToolArgumentRepair
{
    /// <summary>
    /// Parses and, where necessary, repairs a raw argument string.
    /// </summary>
    /// <param name="raw">The argument JSON exactly as the model emitted it.</param>
    /// <param name="schema">The tool's parameter schema, used to coerce types.</param>
    /// <returns>The repaired arguments, or a failure carrying the reason.</returns>
    public static ToolArgumentRepairResult Repair(string raw, JsonNode? schema = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new ToolArgumentRepairResult(new JsonObject(), "empty arguments read as {}");

        var repairs = new List<string>();
        var node = TryParse(raw);

        if (node is null)
        {
            var closed = CloseTruncated(raw);
            node = closed is null ? null : TryParse(closed);
            if (node is null)
                return new ToolArgumentRepairResult(
                    null, "arguments are not valid JSON and could not be repaired");
            repairs.Add("closed truncated JSON");
        }

        if (node is not JsonObject obj)
            return new ToolArgumentRepairResult(null, "arguments must be a JSON object");

        if (schema is not null) CoerceTypes(obj, schema, repairs);

        return new ToolArgumentRepairResult(
            obj, repairs.Count == 0 ? null : string.Join("; ", repairs));
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Closes a value the model stopped emitting part-way through: an unterminated
    /// string, then any still-open objects and arrays, in the order they opened.
    /// </summary>
    private static string? CloseTruncated(string raw)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;

        foreach (var c in raw)
        {
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') stack.Push('}');
            else if (c == '[') stack.Push(']');
            else if (c is '}' or ']' && (stack.Count == 0 || stack.Pop() != c)) return null;
        }

        if (!inString && stack.Count == 0) return null;

        var builder = new StringBuilder(raw.TrimEnd());
        // A trailing comma or a dangling key would leave the close invalid.
        while (builder.Length > 0 && (builder[^1] == ',' || builder[^1] == ':'))
            builder.Length--;
        if (inString) builder.Append('"');
        while (stack.Count > 0) builder.Append(stack.Pop());
        return builder.ToString();
    }

    /// <summary>
    /// Coerces primitives the model got the wrong way round: a number sent as a
    /// string, or a boolean sent as "true". Anything less obvious is left alone,
    /// so this never rejects — the caller used to branch on a false it could
    /// never receive.
    /// </summary>
    /// <param name="arguments">The arguments to repair in place.</param>
    /// <param name="schema">The tool's parameter schema.</param>
    /// <param name="repairs">Where each repair is recorded.</param>
    private static void CoerceTypes(JsonObject arguments, JsonNode schema, List<string> repairs)
    {
        if (schema["properties"] is not JsonObject properties) return;

        foreach (var (name, definition) in properties)
        {
            if (definition?["type"]?.GetValue<string>() is not { } expected) continue;
            if (!arguments.TryGetPropertyValue(name, out var value) || value is null) continue;
            if (value is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var text)) continue;

            // Anything less obvious than a mistyped primitive is left alone.
            if (expected is "number" or "integer" && double.TryParse(text, out var number))
            {
                arguments[name] = JsonValue.Create(number);
                repairs.Add($"coerced '{name}' from string to {expected}");
            }
            else if (expected == "boolean" && bool.TryParse(text, out var flag))
            {
                arguments[name] = JsonValue.Create(flag);
                repairs.Add($"coerced '{name}' from string to boolean");
            }
        }
    }
}
