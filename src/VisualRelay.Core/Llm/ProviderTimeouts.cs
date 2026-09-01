namespace VisualRelay.Core.Llm;

/// <summary>
/// Four separate budgets, replacing the single timeout knob this had before. The
/// old per-model ceilings rested on a false premise: the "up to ~410 s to first
/// token" that justified a 600 s stream timeout was a NON-streaming observation.
/// Streaming time-to-first-byte was measured at 0.43 s, 1.12 s and 0.87 s across
/// the three reasoning models, with a maximum inter-chunk gap of 1.12 s across
/// 1502 chunks: reasoning streams as it is produced.
/// </summary>
/// <param name="Connect">
/// TCP connect budget, enforced by the handler. Short: a provider that cannot be
/// reached in ten seconds is not going to answer.
/// </param>
/// <param name="TimeToFirstByte">
/// From request sent to first byte received. Generous against a measured worst
/// case near one second.
/// </param>
/// <param name="InterChunkIdle">
/// The real stall detector, and the budget that matters. A thirty-second gap
/// between chunks has a 30x margin over the worst gap ever measured, so it fires
/// only on a genuine wedge. This is what would have cut the eighteen- and
/// thirty-minute byte-0 hangs at half a minute instead of at the ceiling.
/// </param>
/// <param name="Total">
/// Whole-request wall clock, the last backstop. Only a runaway response should
/// ever reach it.
/// </param>
public sealed record ProviderTimeouts(
    TimeSpan Connect,
    TimeSpan TimeToFirstByte,
    TimeSpan InterChunkIdle,
    TimeSpan Total)
{
    /// <summary>The measured defaults: 10 s, 30 s, 45 s and 600 s.</summary>
    public static ProviderTimeouts Default { get; } = new(
        Connect: TimeSpan.FromSeconds(10),
        TimeToFirstByte: TimeSpan.FromSeconds(30),
        InterChunkIdle: TimeSpan.FromSeconds(45),
        Total: TimeSpan.FromSeconds(600));
}
