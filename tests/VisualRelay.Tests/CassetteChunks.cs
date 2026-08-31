using System.Runtime.CompilerServices;

namespace VisualRelay.Tests;

/// <summary>
/// Replays a recorded chunk list as the byte stream a transport hands back.
/// Shared by <see cref="ReplayTransport"/> and the in-memory stub so both deliver
/// chunks the same way: in order, unaltered, and one continuation apart, which
/// keeps a consumer that depends on chunks arriving asynchronously behaving as it
/// does against a live provider.
/// </summary>
internal static class CassetteChunks
{
    /// <summary>Yields each chunk in order.</summary>
    /// <param name="chunks">The recorded chunks, in arrival order.</param>
    /// <param name="cancellationToken">Cancels the replay mid-stream.</param>
    /// <returns>The chunk sequence.</returns>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> Emit(
        IReadOnlyList<byte[]> chunks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return chunk;
        }
    }
}
