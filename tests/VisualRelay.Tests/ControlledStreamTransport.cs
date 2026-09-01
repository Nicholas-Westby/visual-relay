using System.Text;
using System.Threading.Channels;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// An <see cref="IProviderTransport"/> whose stream is driven explicitly by the
/// test rather than by a timer.
/// <para>
/// An earlier version scripted each chunk behind <c>Task.Delay</c> on the same
/// manual clock the client uses for its budgets. That looked tidy and was
/// unusable: the scripted delay is armed on a continuation, some virtual time
/// after the client arms its budget, and the skew depends on thread-pool
/// scheduling. The result was a test that passed once in eight runs. Here the
/// test advances the clock to simulate a gap and then hands over the chunk, so
/// ordering is fixed and nothing races.
/// </para>
/// </summary>
/// <param name="statusCode">The status the response reports.</param>
internal sealed class ControlledStreamTransport(
    int statusCode, IReadOnlyDictionary<string, string>? headers = null) : IProviderTransport
{
    // AllowSynchronousContinuations is what makes this deterministic. Without it
    // the reader's continuation is posted to the thread pool, and a test that
    // "drains" with Task.Yield never observes it: yielding returns to xUnit's
    // synchronization context, not to the pool, so the chunk appears only once
    // real time passes. With it, Emit completes the pending read inline on the
    // calling thread, so by the time Emit returns the client has consumed it.
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { AllowSynchronousContinuations = true });

    /// <summary>The last request handed to this transport.</summary>
    public ProviderRequest? LastRequest { get; private set; }

    /// <summary>Hands the client one chunk of stream bytes.</summary>
    /// <param name="text">The raw chunk text.</param>
    public void Emit(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    /// <summary>Ends the stream, as a provider closing the connection would.</summary>
    public void Complete() => _chunks.Writer.TryComplete();

    /// <inheritdoc />
    public Task<ProviderResponse> SendAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(new ProviderResponse(
            statusCode, headers ?? new Dictionary<string, string>(), string.Empty));
    }

    /// <inheritdoc />
    public Task<ProviderStreamResponse> StreamAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(new ProviderStreamResponse(
            statusCode, headers ?? new Dictionary<string, string>(), ReadAsync));
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return chunk;
    }
}
