using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Traces;

public static class RelayTraceParser
{
    public static IReadOnlyList<TraceEntry> Parse(string jsonl)
    {
        var entries = new List<TraceEntry>();
        foreach (var line in jsonl.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                AppendRecord(doc.RootElement, entries);
            }
            catch (JsonException)
            {
                // Skip malformed trace lines.
            }
        }

        return entries;
    }

    private static void AppendRecord(JsonElement record, List<TraceEntry> entries)
    {
        var type = record.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
        if (type is "system" or "last-prompt")
        {
            return;
        }

        if (type == "user")
        {
            AppendContent(record, entries, includeUserText: true);
            return;
        }

        if (type == "assistant")
        {
            AppendContent(record, entries, includeUserText: false);
        }
    }

    private static void AppendContent(JsonElement record, List<TraceEntry> entries, bool includeUserText)
    {
        if (!record.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            entries.Add(new TraceEntry(includeUserText ? TraceEntryKind.UserText : TraceEntryKind.AssistantText, "text", content.GetString() ?? string.Empty));
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            AppendBlock(block, entries, includeUserText);
        }
    }

    private static void AppendBlock(JsonElement block, List<TraceEntry> entries, bool includeUserText)
    {
        var type = block.TryGetProperty("type", out var value) ? value.GetString() : null;
        if (type == "text")
        {
            entries.Add(new TraceEntry(includeUserText ? TraceEntryKind.UserText : TraceEntryKind.AssistantText, "text", ReadString(block, "text", "content")));
        }
        else if (type == "tool_use")
        {
            var name = ReadString(block, "name");
            var input = block.TryGetProperty("input", out var rawInput) ? rawInput.GetRawText() : string.Empty;
            entries.Add(new TraceEntry(TraceEntryKind.ToolCall, name, input));
        }
        else if (type is "thinking" or "reasoning")
        {
            entries.Add(new TraceEntry(TraceEntryKind.Thinking, "thinking", ReadString(block, "thinking", "text", "content")));
        }
        else if (type == "tool_result")
        {
            entries.Add(new TraceEntry(TraceEntryKind.ToolResult, "tool_result", ReadString(block, "content")));
        }
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
            }
        }

        return string.Empty;
    }
}
