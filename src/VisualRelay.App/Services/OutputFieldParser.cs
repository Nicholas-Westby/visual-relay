using System.Text.Json;
using VisualRelay.Core.Execution;

namespace VisualRelay.App.Services;

public enum OutputFieldKind { Text, List, Json }

public sealed record OutputField(string Label, OutputFieldKind Kind, string Value);

public sealed record OutputParseResult(IReadOnlyList<OutputField> Fields, string RawJson);

public static class OutputFieldParser
{
    private static readonly JsonSerializerOptions PrettyPrintOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Parses a stage output string (which may be JSON, a fenced JSON block,
    /// or plain text) into labelled fields and raw JSON.
    /// </summary>
    public static OutputParseResult Parse(string? stageOutput)
    {
        if (string.IsNullOrEmpty(stageOutput))
            return new OutputParseResult([], "");

        // Try extracting a fenced JSON block first (models often wrap JSON).
        var jsonText = FencedJsonExtractor.Extract(stageOutput) ?? stageOutput;

        // Try parsing as JSON.
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                // Not a JSON object — treat as plain text.
                return SingleTextField(stageOutput);
            }

            var fields = new List<OutputField>();
            foreach (var prop in root.EnumerateObject())
            {
                var (kind, value) = MapElement(prop.Value);
                fields.Add(new OutputField(prop.Name, kind, value));
            }

            var rawJson = JsonSerializer.Serialize(root, PrettyPrintOptions);
            return new OutputParseResult(fields, rawJson);
        }
        catch (JsonException)
        {
            // Not valid JSON — treat as plain text.
            return SingleTextField(stageOutput);
        }
    }

    private static (OutputFieldKind Kind, string Value) MapElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => (OutputFieldKind.Text, element.GetString()!),
            JsonValueKind.Array => MapArray(element),
            JsonValueKind.Null => (OutputFieldKind.Text, "null"),
            _ => (OutputFieldKind.Json, JsonSerializer.Serialize(element, PrettyPrintOptions))
        };
    }

    private static (OutputFieldKind Kind, string Value) MapArray(JsonElement array)
    {
        // If all elements are strings, treat as List (joined by newlines).
        var allStrings = true;
        var items = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                items.Add(item.GetString()!);
            else
                allStrings = false;
        }

        if (allStrings)
            return (OutputFieldKind.List, string.Join('\n', items));

        // Mixed or non-string array → Json.
        return (OutputFieldKind.Json, JsonSerializer.Serialize(array, PrettyPrintOptions));
    }

    private static OutputParseResult SingleTextField(string raw)
    {
        return new OutputParseResult(
            [new OutputField("Output", OutputFieldKind.Text, raw)],
            raw);
    }
}
