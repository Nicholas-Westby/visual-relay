using System.Text.Json;

namespace VisualRelay.Core.Llm;

/// <summary>
/// Drives one streaming chat completion over the transport seam: frames the SSE,
/// folds the deltas, reads measured usage, and enforces the three budgets that
/// cannot be expressed as a single HTTP timeout.
/// <para>
/// The two clocks are deliberately separate. A keepalive comment proves the
/// connection is alive, so it feeds the inter-chunk idle budget; it is not model
/// output, so it does NOT reset the output clock the watchdog reads. Collapsing
/// them is how a wedged request once survived to the absolute ceiling.
/// </para>
/// </summary>
public sealed class ChatCompletionClient
{
    private readonly IProviderTransport _transport;
    private readonly ProviderTimeouts _timeouts;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a client over a transport.</summary>
    /// <param name="transport">The seam to send through.</param>
    /// <param name="timeouts">Budgets; defaults to the measured set.</param>
    /// <param name="timeProvider">Clock, for virtual-time tests.</param>
    public ChatCompletionClient(
        IProviderTransport transport,
        ProviderTimeouts? timeouts = null,
        TimeProvider? timeProvider = null)
    {
        _transport = transport;
        _timeouts = timeouts ?? ProviderTimeouts.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Streams one completion.
    /// </summary>
    /// <param name="request">The wire-level request, already shaped.</param>
    /// <param name="onOutput">
    /// Called with each content delta as it arrives, so the UI can show model
    /// output while the stage is still running.
    /// </param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The finished turn.</returns>
    public async Task<ChatCompletion> StreamAsync(
        ProviderRequest request,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Declared after the source so it is disposed first: cancelling a
        // disposed source throws, and the timer callback can be in flight.
        await using var totalTimer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            totalCts,
            _timeouts.Total,
            Timeout.InfiniteTimeSpan);

        var accumulator = new ChatDeltaAccumulator();
        var parser = new SseFrameParser();

        try
        {
            await using var response = await _transport
                .StreamAsync(request, totalCts.Token).ConfigureAwait(false);

            // Status is available before any chunk, so an error short circuits
            // before the parser sees anything. A 200 is not yet a success: it can
            // carry an error body, which the fold below also probes for.
            if (response.StatusCode is < 200 or >= 300)
                return Failed(await ReadErrorAsync(response, totalCts.Token).ConfigureAwait(false));

            var error = await FoldAsync(response, parser, accumulator, onOutput, totalCts.Token)
                .ConfigureAwait(false);
            if (error is not null) return Failed(error, accumulator);

            if (!parser.SawDone) return Build(CompletionOutcome.Truncated, accumulator);

            return Build(Classify(accumulator), accumulator);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A budget expired rather than the caller stopping us.
            return Build(CompletionOutcome.TimedOut, accumulator);
        }
    }

    private async Task<ProviderError?> FoldAsync(
        ProviderStreamResponse response,
        SseFrameParser parser,
        ChatDeltaAccumulator accumulator,
        Action<string>? onOutput,
        CancellationToken cancellationToken)
    {
        await foreach (var chunk in WithIdleBudgetAsync(response, first: true, cancellationToken)
            .ConfigureAwait(false))
        {
            foreach (var frame in parser.Append(chunk.Span))
            {
                // A keepalive advances the idle clock only, which happens by
                // virtue of the chunk arriving. Nothing else to do with it.
                if (frame.Kind != SseFrameKind.Event) continue;

                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(frame.Data);
                    root = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // A malformed frame is not fatal on its own; skip it and let
                    // the missing terminator mark the stream truncated.
                    continue;
                }

                // Hugging Face signals a mid-stream failure on an already-200
                // response by putting an error key in a chunk. Probed from the
                // element already parsed above, not by re-parsing the frame.
                var error = ProviderErrorReader.TryRead(200, root);
                if (error is not null) return error;

                var added = accumulator.Append(root);
                if (added.Length > 0) onOutput?.Invoke(added);
            }
        }

        foreach (var frame in parser.Flush())
            if (frame.Kind == SseFrameKind.Event)
                try
                {
                    using var document = JsonDocument.Parse(frame.Data);
                    accumulator.Append(document.RootElement);
                }
                catch (JsonException)
                {
                    // Same as above: a trailing partial frame is not fatal.
                }

        return null;
    }

    /// <summary>
    /// Wraps the chunk sequence so a stalled provider fails on its own budget
    /// rather than on the whole-request ceiling. The first gap is the
    /// time-to-first-byte budget; every later one is the inter-chunk idle budget.
    /// </summary>
    private async IAsyncEnumerable<ReadOnlyMemory<byte>> WithIdleBudgetAsync(
        ProviderStreamResponse response,
        bool first,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The read gets its own token so a expired budget can CANCEL the in-flight
        // read rather than abandon it. Disposing an async iterator while its
        // MoveNextAsync is still pending throws NotSupportedException, which would
        // replace a clean timeout with a confusing crash.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = response.ReadChunksAsync(readCts.Token)
            .GetAsyncEnumerator(readCts.Token);

        while (true)
        {
            var budget = first ? _timeouts.TimeToFirstByte : _timeouts.InterChunkIdle;
            var next = enumerator.MoveNextAsync().AsTask();
            using var expiryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var expiry = Task.Delay(budget, _timeProvider, expiryCts.Token);

            if (await Task.WhenAny(next, expiry).ConfigureAwait(false) == expiry)
            {
                await readCts.CancelAsync().ConfigureAwait(false);
                // Let the cancelled read finish before the enumerator is disposed.
                try
                {
                    await next.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected: this is the read we just cancelled.
                }

                throw new OperationCanceledException(
                    $"provider sent no bytes for {budget.TotalSeconds:0}s");
            }

            // Retire the losing timer so a long stream does not accumulate one
            // pending delay per chunk.
            await expiryCts.CancelAsync().ConfigureAwait(false);

            if (!await next.ConfigureAwait(false)) yield break;
            first = false;
            yield return enumerator.Current;
        }
    }

    private static CompletionOutcome Classify(ChatDeltaAccumulator accumulator) =>
        accumulator.FinishReason == "length" && accumulator.Content().Length == 0
            ? CompletionOutcome.LengthWithoutContent
            : CompletionOutcome.Completed;

    /// <summary>
    /// Drains an error body under the idle budget rather than the whole-request
    /// one. A provider that answers 429 and then stops writing must fail in
    /// seconds, not hold the turn open for the total ceiling.
    /// </summary>
    private async Task<ProviderError> ReadErrorAsync(
        ProviderStreamResponse response, CancellationToken cancellationToken)
    {
        var body = new System.Text.StringBuilder();
        try
        {
            await foreach (var chunk in WithIdleBudgetAsync(response, first: true, cancellationToken)
                .ConfigureAwait(false))
                body.Append(System.Text.Encoding.UTF8.GetString(chunk.Span));
        }
        catch (OperationCanceledException)
        {
            // Whatever arrived before the stall is still the best description.
        }

        return ProviderErrorReader.TryRead(response.StatusCode, body.ToString())
            ?? new ProviderError(response.StatusCode, null, body.ToString(), ProviderErrorKind.Unknown);
    }

    private static ChatCompletion Failed(ProviderError error, ChatDeltaAccumulator? accumulator = null) =>
        Build(CompletionOutcome.Failed, accumulator ?? new ChatDeltaAccumulator(), error);

    private static ChatCompletion Build(
        CompletionOutcome outcome, ChatDeltaAccumulator accumulator, ProviderError? error = null) =>
        new(outcome,
            accumulator.Content(),
            accumulator.ReasoningContent(),
            accumulator.ToolCalls(),
            accumulator.FinishReason,
            accumulator.Usage,
            accumulator.ServedModel,
            error);
}
