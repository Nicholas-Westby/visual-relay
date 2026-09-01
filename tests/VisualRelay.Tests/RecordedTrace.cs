using System.Text.Json;

namespace VisualRelay.Tests;

/// <summary>One tool call the recorded model asked for.</summary>
/// <param name="Name">The tool it named.</param>
/// <param name="Arguments">The arguments it sent, canonicalized.</param>
public sealed record RecordedToolCall(string Name, string Arguments);

/// <summary>One assistant turn from a recorded trace.</summary>
/// <param name="Text">Any assistant text in the turn.</param>
/// <param name="ToolCalls">The tool calls it asked for, in order.</param>
public sealed record RecordedTurn(string Text, IReadOnlyList<RecordedToolCall> ToolCalls);

/// <summary>
/// Reads a JSONL transcript recorded by the previous runner.
/// <para>
/// These are the ground truth for the offline differential: 967 of them sit in
/// <c>.relay/</c>, covering every stage and every failure mode that has actually
/// occurred here. Replaying their model turns through the new loop and comparing
/// the tool calls it makes is free, deterministic and needs no provider.
/// </para>
/// </summary>
public static class RecordedTrace
{
    /// <summary>
    /// The assistant turns of one recorded trace, in order.
    /// </summary>
    /// <param name="path">The trace file.</param>
    /// <returns>The turns, oldest first.</returns>
    public static IReadOnlyList<RecordedTurn> ReadTurns(string path)
    {
        var turns = new List<RecordedTurn>();

        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // A malformed line is skipped, exactly as the existing parser does.
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "assistant")
                    continue;
                if (!root.TryGetProperty("message", out var message)
                    || !message.TryGetProperty("content", out var content)
                    || content.ValueKind != JsonValueKind.Array)
                    continue;

                var text = new System.Text.StringBuilder();
                var calls = new List<RecordedToolCall>();

                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    var kind = block.TryGetProperty("type", out var t) ? t.GetString() : null;

                    if (kind == "text" && block.TryGetProperty("text", out var value))
                        text.Append(value.GetString());
                    else if (kind == "tool_use"
                        && block.TryGetProperty("name", out var name)
                        && block.TryGetProperty("input", out var input))
                        calls.Add(new RecordedToolCall(
                            name.GetString() ?? string.Empty, Canonical(input)));
                }

                turns.Add(new RecordedTurn(text.ToString(), calls));
            }
        }

        return turns;
    }

    /// <summary>
    /// Every recorded trace under a directory, newest first so a sample is
    /// drawn from recent behaviour rather than the oldest runs.
    /// </summary>
    /// <param name="root">The repository root holding <c>.relay</c>.</param>
    /// <returns>Trace file paths.</returns>
    public static IReadOnlyList<string> Discover(string root)
    {
        var relay = Path.Combine(root, ".relay");
        if (!Directory.Exists(relay)) return [];

        return [.. Directory.EnumerateFiles(relay, "*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)];
    }

    /// <summary>Re-serializes a value with sorted keys, so comparison is order-independent.</summary>
    private static string Canonical(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return element.GetRawText();

        var sorted = new System.Text.Json.Nodes.JsonObject();
        foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            sorted[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());

        return sorted.ToJsonString();
    }
}
