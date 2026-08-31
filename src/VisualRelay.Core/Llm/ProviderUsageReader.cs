using System.Text.Json;

namespace VisualRelay.Core.Llm;

/// <summary>
/// Finds the <c>usage</c> object wherever a provider chose to put it. Measured
/// on 2026-08-31, the four providers disagree three ways inside one nominal wire
/// format:
/// <list type="bullet">
/// <item>DeepSeek and Z.AI: top level, on the <c>finish_reason</c> chunk.</item>
/// <item>Moonshot: nested in <c>choices[0].usage</c> on the finish chunk, AND
/// again at the top level on a trailing empty-choices chunk.</item>
/// <item>Hugging Face: top level, on a trailing <c>choices: []</c> chunk.</item>
/// </list>
/// Because Moonshot reports it twice, a reader that accumulates would double
/// every Moonshot bill. <see cref="Merge"/> therefore takes last-writer-wins over
/// the union of both locations rather than summing.
/// </summary>
public static class ProviderUsageReader
{
    /// <summary>
    /// Reads usage from one chunk or one complete response, checking both places
    /// any provider uses.
    /// </summary>
    /// <param name="root">The parsed chunk or response object.</param>
    /// <returns>The usage, or <c>null</c> when this element carries none.</returns>
    public static ProviderUsage? TryRead(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        if (TryReadObject(root, "usage", out var usage)) return usage;

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && TryReadObject(choices[0], "usage", out var nested))
            return nested;

        return null;
    }

    /// <summary>
    /// Combines a running value with a newly seen one. The later report wins
    /// outright; nothing is summed, because a provider repeating its usage is
    /// restating the same call, not reporting a second one.
    /// </summary>
    /// <param name="current">The usage seen so far, if any.</param>
    /// <param name="next">The newly read usage, if any.</param>
    /// <returns>The value to carry forward.</returns>
    public static ProviderUsage? Merge(ProviderUsage? current, ProviderUsage? next) => next ?? current;

    private static bool TryReadObject(JsonElement parent, string name, out ProviderUsage? usage)
    {
        usage = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
            return false;

        var prompt = ReadInt(element, "prompt_tokens");
        var completion = ReadInt(element, "completion_tokens");

        // Hugging Face sends both details objects as explicit JSON nulls rather
        // than omitting them, so presence is not enough: the value must actually
        // be an object before anything is read out of it.
        var cached = ReadInt(Child(element, "prompt_tokens_details"), "cached_tokens");
        // DeepSeek also reports the split directly; prefer the explicit hit count
        // when the details object omitted it.
        if (cached == 0) cached = ReadInt(element, "prompt_cache_hit_tokens");

        var reasoning = ReadInt(Child(element, "completion_tokens_details"), "reasoning_tokens");

        var cacheWrite = ReadInt(element, "cache_creation_input_tokens");

        usage = new ProviderUsage(prompt, completion, cached, reasoning, cacheWrite);
        return true;
    }

    /// <summary>The named child when it is an object, else a null-kind element.</summary>
    private static JsonElement Child(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child
            : default;

    private static int ReadInt(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
        && parent.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : 0;
}
