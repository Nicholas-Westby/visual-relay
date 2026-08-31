using System.Text;
using System.Text.Json;

namespace VisualRelay.Core.Llm;

/// <summary>
/// Accumulates streamed chat deltas into a finished turn.
/// <para>
/// Tool calls are keyed strictly on <c>index</c>, never on <c>id</c>. Hugging
/// Face emits a duplicate <c>id</c> carrying an empty <c>function</c> object, so
/// a client that starts a new call whenever it sees an id invents a phantom
/// call. Z.AI, at the other extreme, sends a whole tool call in a single delta,
/// so any logic assuming the first delta has empty arguments is also wrong.
/// Indexing satisfies both.
/// </para>
/// </summary>
public sealed class ChatDeltaAccumulator
{
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly SortedDictionary<int, ToolCallBuilder> _toolCalls = [];

    /// <summary>The model's finish reason, once it has sent one.</summary>
    public string? FinishReason { get; private set; }

    /// <summary>The concrete model the provider reported serving.</summary>
    public string? ServedModel { get; private set; }

    /// <summary>Measured usage, last writer winning over the union of locations.</summary>
    public ProviderUsage? Usage { get; private set; }

    /// <summary>True once any content or reasoning text has arrived.</summary>
    public bool SawOutput { get; private set; }

    /// <summary>
    /// Folds one parsed SSE event payload into the turn.
    /// </summary>
    /// <param name="root">The parsed chunk object.</param>
    /// <returns>The content text this chunk added, empty when it added none.</returns>
    public string Append(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return string.Empty;

        if (root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
            ServedModel = model.GetString();

        Usage = ProviderUsageReader.Merge(Usage, ProviderUsageReader.TryRead(root));

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return string.Empty;

        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finish)
            && finish.ValueKind == JsonValueKind.String)
            FinishReason = finish.GetString();

        if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            return string.Empty;

        var added = string.Empty;
        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            added = content.GetString() ?? string.Empty;
            if (added.Length > 0)
            {
                _content.Append(added);
                SawOutput = true;
            }
        }

        if (delta.TryGetProperty("reasoning_content", out var reasoning)
            && reasoning.ValueKind == JsonValueKind.String)
        {
            var text = reasoning.GetString();
            if (!string.IsNullOrEmpty(text))
            {
                _reasoning.Append(text);
                SawOutput = true;
            }
        }

        if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            foreach (var call in calls.EnumerateArray()) AppendToolCall(call);

        return added;
    }

    private void AppendToolCall(JsonElement call)
    {
        if (call.ValueKind != JsonValueKind.Object) return;

        // Index is the identity. A delta without one is malformed; treat it as
        // the first call rather than dropping the fragment.
        var index = call.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number
            ? i.GetInt32()
            : 0;

        if (!_toolCalls.TryGetValue(index, out var builder))
            _toolCalls[index] = builder = new ToolCallBuilder();

        // An id may be repeated across deltas; keep the first non-empty one.
        if (call.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
        {
            var text = id.GetString();
            if (!string.IsNullOrEmpty(text) && string.IsNullOrEmpty(builder.Id)) builder.Id = text;
        }

        if (!call.TryGetProperty("function", out var function)
            || function.ValueKind != JsonValueKind.Object)
            return;

        if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
        {
            var text = name.GetString();
            if (!string.IsNullOrEmpty(text)) builder.Name = text;
        }

        if (function.TryGetProperty("arguments", out var arguments)
            && arguments.ValueKind == JsonValueKind.String)
            builder.Arguments.Append(arguments.GetString());
    }

    /// <summary>The accumulated assistant content.</summary>
    /// <returns>The content text, possibly empty.</returns>
    public string Content() => _content.ToString();

    /// <summary>The accumulated reasoning trace, or null when none arrived.</summary>
    /// <returns>The reasoning text, or null.</returns>
    public string? ReasoningContent() => _reasoning.Length == 0 ? null : _reasoning.ToString();

    /// <summary>
    /// The finished tool calls, in index order. A call whose fragments never
    /// carried a name is dropped: it is a phantom rather than a request.
    /// </summary>
    /// <returns>The tool calls the model asked for.</returns>
    public IReadOnlyList<ToolCall> ToolCalls() =>
        [.. _toolCalls.Values
            .Where(b => !string.IsNullOrEmpty(b.Name))
            .Select(b => new ToolCall(b.Id ?? string.Empty, b.Name!, b.Arguments.ToString()))];

    private sealed class ToolCallBuilder
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
