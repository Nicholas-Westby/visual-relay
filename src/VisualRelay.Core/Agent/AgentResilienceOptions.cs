namespace VisualRelay.Core.Agent;

/// <summary>
/// Tuning for the four resilience mechanisms the recorded corpus actually
/// justifies. Every default below is the behaviour observed across 1109 stages;
/// everything the corpus showed never firing was deliberately not ported.
/// </summary>
/// <param name="StormWindow">
/// How many recent calls the repeat detector looks back over.
/// </param>
/// <param name="StormThreshold">
/// How many identical calls inside that window count as a storm. The previous
/// implementation hardcoded 6 and 3 with no way to change either, which
/// suppressed 41 real calls including the legitimate case of re-running one
/// test command to see whether it is flaky. Exposing both is the escape hatch
/// that was missing.
/// </param>
/// <param name="ConsecutiveErrorLimit">
/// How many tool errors in a row before the loop intervenes. Fired 95 times
/// across the corpus, so it earns its place.
/// </param>
/// <param name="MaxCompactions">
/// How many times one stage may compact its context. Compaction fired 414
/// times across the corpus.
/// </param>
public sealed record AgentResilienceOptions(
    int StormWindow = 6,
    int StormThreshold = 3,
    int ConsecutiveErrorLimit = 5,
    int MaxCompactions = 3)
{
    /// <summary>The defaults, matching the behaviour the corpus recorded.</summary>
    public static AgentResilienceOptions Default { get; } = new();
}
