using System.Text.Json.Nodes;
using VisualRelay.Core.Llm;

namespace VisualRelay.Core.Agent;

public sealed partial class AgentTurnLoop
{
    /// <summary>
    /// Asks the model once, retrying the failures that retrying can fix.
    /// <para>
    /// Three outcomes are retryable and each for a different reason: a rate limit
    /// or a provider fault, where waiting helps; a truncated stream, where the
    /// connection died mid-answer; and a length finish with no content at all,
    /// which on a reasoning model means the whole budget went to reasoning before
    /// a single content token was emitted. That last one is retried with a larger
    /// budget rather than reported as an empty answer.
    /// </para>
    /// </summary>
    private async Task<ChatCompletion?> CallModelAsync(
        List<ChatMessage> messages,
        IReadOnlyList<JsonNode> tools,
        AgentLoopOptions options,
        LoopState state,
        CancellationToken cancellationToken)
    {
        int? maxTokens = null;

        for (var attempt = 0; attempt <= options.RetryBudget; attempt++)
        {
            var request = new ProviderRequest(
                HttpMethod.Post,
                options.Endpoint,
                options.Headers,
                ChatRequestBuilder.Build(
                    messages,
                    new ChatRequestOptions(options.Model, Stream: true, MaxTokens: maxTokens),
                    options.Capabilities,
                    tools));

            var startedAt = _timeProvider.GetTimestamp();
            state.LlmCalls++;

            var completion = await _client.StreamAsync(
                request,
                text => Publish(AgentEventKind.TokenDelta, state.Turn, text: text),
                cancellationToken).ConfigureAwait(false);

            state.LlmSeconds += _timeProvider.GetElapsedTime(startedAt).TotalSeconds;
            state.AddUsage(completion.Usage);
            if (completion.Usage is { PromptTokens: > 0 } measured)
                state.LastPromptTokens = measured.PromptTokens;
            if (completion.ServedModel is { Length: > 0 }) state.ServedModel = completion.ServedModel;

            if (completion.Usage is not null)
                Publish(AgentEventKind.Usage, state.Turn,
                    usage: completion.Usage, model: completion.ServedModel);

            if (completion.ReasoningContent is { Length: > 0 } reasoning)
                Publish(AgentEventKind.ReasoningDelta, state.Turn, text: reasoning);

            switch (completion.Outcome)
            {
                case CompletionOutcome.Completed:
                    return completion;

                case CompletionOutcome.LengthWithoutContent:
                    // Every output token went to reasoning. Give it more room
                    // rather than escalating a tier over a budget problem.
                    maxTokens = maxTokens is null ? 8192 : maxTokens * 2;
                    state.LastError = "model produced only reasoning within its output budget";
                    break;

                case CompletionOutcome.Truncated:
                    state.LastError = "provider ended the stream without its terminator";
                    break;

                case CompletionOutcome.TimedOut:
                    state.LastError = "provider stalled past its idle budget";
                    break;

                case CompletionOutcome.Failed:
                default:
                    state.LastError = completion.Error?.Message ?? "provider call failed";
                    // Auth, a dead model and an exhausted quota cannot be fixed by
                    // trying again; retrying an exhausted quota just burns budget.
                    if (completion.Error is { IsRetryable: false }) return null;
                    break;
            }

            if (attempt == options.RetryBudget) return null;

            state.Retries++;
            Publish(AgentEventKind.Retry, state.Turn,
                text: state.LastError, detail: $"attempt {attempt + 1} of {options.RetryBudget}");

            await BackoffAsync(attempt, options, completion.Error?.RetryAfter, cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// How long to wait before the next attempt.
    /// </summary>
    /// <param name="attempt">The attempt just finished, zero-based.</param>
    /// <param name="options">The loop's backoff base.</param>
    /// <param name="retryAfter">What the provider asked for, when it asked.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The completed wait.</returns>
    /// <remarks>
    /// A provider's own <c>Retry-After</c> wins, exactly, with no jitter added:
    /// it knows when its window reopens and guessing over it is how a rate limit
    /// becomes a ban. None of the four was ever observed sending one, so in
    /// practice this is exponential backoff with jitter — the only schedule
    /// available when the provider says nothing.
    /// </remarks>
    private Task BackoffAsync(
        int attempt, AgentLoopOptions options, TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        if (retryAfter is { } asked)
            return asked <= TimeSpan.Zero
                ? Task.CompletedTask
                : Task.Delay(asked, _timeProvider, cancellationToken);

        if (options.BackoffBase <= TimeSpan.Zero) return Task.CompletedTask;

        var baseDelay = options.BackoffBase * Math.Pow(2, attempt);
        var jitter = TimeSpan.FromMilliseconds(
            Random.Shared.Next(0, (int)Math.Max(1, options.BackoffBase.TotalMilliseconds)));
        return Task.Delay(baseDelay + jitter, _timeProvider, cancellationToken);
    }
}
