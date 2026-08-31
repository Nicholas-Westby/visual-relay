using VisualRelay.Core.Llm;

namespace VisualRelay.Core.Agent;

/// <summary>The result of one compaction pass.</summary>
/// <param name="Messages">The conversation to send next.</param>
/// <param name="DroppedMessages">How many messages were removed.</param>
/// <param name="Note">
/// What the model is told, so it knows why earlier detail is gone rather than
/// concluding it imagined reading a file.
/// </param>
public sealed record CompactionResult(
    IReadOnlyList<ChatMessage> Messages, int DroppedMessages, string? Note);

/// <summary>
/// Trims a conversation that is approaching the model's context window.
/// Compaction fired 414 times across the recorded corpus, more than any other
/// resilience mechanism, so it is ported; the machinery that fired zero times is
/// not.
/// <para>
/// It triggers on the provider's own <c>prompt_tokens</c> from the previous
/// call, not on a character estimate. The window is known for every model in the
/// catalog, so there is nothing to learn adaptively and no reason to guess.
/// </para>
/// <para>
/// Tool results are dropped first and oldest first. They are the bulk of a long
/// conversation and the least useful once acted on, whereas the opening message
/// carries the stage contract and the recent turns carry the work in progress.
/// </para>
/// </summary>
/// <param name="contextWindow">The model's context window in tokens.</param>
/// <param name="options">How many compactions are allowed; defaults to the measured set.</param>
public sealed class ContextCompactor(int contextWindow, AgentResilienceOptions? options = null)
{
    private readonly AgentResilienceOptions _options = options ?? AgentResilienceOptions.Default;

    /// <summary>How many times this stage has compacted.</summary>
    public int Compactions { get; private set; }

    /// <summary>
    /// Whether the next call should compact first, given what the provider said
    /// the last one cost.
    /// </summary>
    /// <param name="measuredPromptTokens">
    /// <c>prompt_tokens</c> from the previous call. Zero means nothing measured
    /// yet, which is never a reason to compact.
    /// </param>
    /// <returns>True when the conversation should be trimmed.</returns>
    public bool ShouldCompact(int measuredPromptTokens)
    {
        if (measuredPromptTokens <= 0) return false;
        if (Compactions >= _options.MaxCompactions) return false;

        // The ladder tightens: each firing acts earlier than the last, because a
        // conversation that needed compacting once is growing.
        var threshold = contextWindow * (0.80 - (0.05 * Compactions));
        return measuredPromptTokens >= threshold;
    }

    /// <summary>
    /// Trims the conversation. Keeps the opening message, the most recent turns,
    /// and every message that is not a droppable tool result.
    /// </summary>
    /// <param name="messages">The conversation, oldest first.</param>
    /// <returns>The trimmed conversation and what to tell the model.</returns>
    public CompactionResult Compact(IReadOnlyList<ChatMessage> messages)
    {
        // The ladder again: the first pass keeps a generous tail, later ones less.
        var keepRecent = Math.Max(4, 12 - (Compactions * 4));
        if (messages.Count <= keepRecent + 1)
            return new CompactionResult(messages, 0, null);

        var firstKeptIndex = messages.Count - keepRecent;
        var kept = new List<ChatMessage> { messages[0] };
        var dropped = 0;

        for (var i = 1; i < firstKeptIndex; i++)
        {
            // A tool result is droppable; an assistant turn bearing tool calls is
            // not, because removing it orphans the tool_call_id its result cites.
            if (messages[i].Role == "tool")
            {
                dropped++;
                continue;
            }

            kept.Add(messages[i]);
        }

        for (var i = firstKeptIndex; i < messages.Count; i++) kept.Add(messages[i]);

        if (dropped == 0) return new CompactionResult(messages, 0, null);

        Compactions++;
        var note = $"[context compacted: {dropped} earlier tool result(s) removed to stay inside "
            + "the model's context window. Re-read anything you still need.]";
        kept.Insert(1, new ChatMessage("user", note));

        return new CompactionResult(kept, dropped, note);
    }
}
