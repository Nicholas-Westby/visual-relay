using VisualRelay.Core.Agent;

namespace VisualRelay.Tests;

/// <summary>
/// The offline differential: every recorded trace's model turns replayed through
/// the new loop, asserting it issues the same tool calls in the same order.
/// <para>
/// This is free and deterministic — the model side is a recording, so nothing
/// reaches a provider and nothing is spent. The spec is explicit that nothing
/// goes to a paid benchmark until this replays clean, which is why it runs in
/// the fast suite rather than behind an opt-in gate.
/// </para>
/// </summary>
public sealed class TraceReplayDifferentialTests
{
    /// <summary>
    /// How many traces the fast suite replays. The corpus holds nearly a
    /// thousand; replaying every one on every run would cost more than the whole
    /// suite's budget, so the fast suite takes the most recent slice and the
    /// full sweep runs behind the opt-in gate below.
    /// </summary>
    private const int FastSuiteSample = 60;

    private static IReadOnlyList<string> Traces() => RecordedTrace.Discover(RepoSetup.Root);

    /// <summary>
    /// The corpus is present. Without it the differential silently passes by
    /// examining nothing, which is worse than failing.
    /// </summary>
    [Fact]
    public void TheRecordedCorpus_IsPresent()
    {
        Assert.True(Traces().Count > 100,
            $"expected a corpus of recorded traces under .relay; found {Traces().Count}");
    }

    /// <summary>
    /// A sample of recent traces replays with identical tool calls, in order.
    /// A mismatch names the first call that differed.
    /// </summary>
    [Fact]
    public async Task RecentTraces_ReplayWithIdenticalToolCalls()
    {
        var mismatches = new List<string>();
        var replayed = 0;

        foreach (var trace in Traces().Take(FastSuiteSample))
        {
            var result = await TraceReplayHarness.ReplayAsync(trace);

            // A trace with no assistant tool calls exercises nothing; skip it
            // rather than counting it as a pass.
            if (result.Expected.Count == 0) continue;

            replayed++;
            if (!result.Matches)
                mismatches.Add($"{Path.GetFileName(trace)}: {result.Describe()}");
        }

        Assert.True(replayed > 0, "no sampled trace carried any tool call to compare");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {replayed} replayed traces diverged:\n"
            + string.Join("\n", mismatches.Take(10)));
    }

    /// <summary>
    /// A replay that reproduces every recorded call and then answers finishes
    /// successfully, rather than exhausting its turns.
    /// </summary>
    [Fact]
    public async Task ReplayedTraces_FinishRatherThanExhaust()
    {
        var exhausted = new List<string>();

        foreach (var trace in Traces().Take(20))
        {
            var result = await TraceReplayHarness.ReplayAsync(trace);
            if (result.Outcome is AgentLoopOutcome.Exhausted or AgentLoopOutcome.Error)
                exhausted.Add($"{Path.GetFileName(trace)}: {result.Outcome}");
        }

        Assert.True(exhausted.Count == 0,
            "these replays did not finish cleanly:\n" + string.Join("\n", exhausted));
    }

    /// <summary>
    /// The whole corpus, behind the opt-in gate. This is the sweep the spec asks
    /// for before any paid benchmark; it is too slow for every run.
    /// </summary>
    [Fact]
    public async Task EveryRecordedTrace_ReplaysCleanly()
    {
        SlowIntegration.SkipIfNotOptedIn(
            "VR_RUN_SLOW_INTEGRATION=1 required for the full-corpus trace differential.");

        var mismatches = new List<string>();
        var replayed = 0;

        foreach (var trace in Traces())
        {
            var result = await TraceReplayHarness.ReplayAsync(trace);
            if (result.Expected.Count == 0) continue;

            replayed++;
            if (!result.Matches)
                mismatches.Add($"{Path.GetFileName(trace)}: {result.Describe()}");
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {replayed} replayed traces diverged:\n"
            + string.Join("\n", mismatches.Take(25)));
    }

    /// <summary>
    /// The tools the new set deliberately dropped really are rare. The spec
    /// calls skills and subagents "unused"; measured across the whole corpus
    /// they are used, just seldom, and this pins how seldom so a future decision
    /// to restore one rests on a number rather than a recollection.
    /// </summary>
    [Fact]
    public async Task DroppedTools_AreRareEnoughToJustifyDroppingThem()
    {
        var unported = 0;
        var compared = 0;

        foreach (var trace in Traces().Take(FastSuiteSample))
        {
            var result = await TraceReplayHarness.ReplayAsync(trace);
            unported += result.UnportedCalls.Count;
            compared += result.Expected.Count;
        }

        Assert.True(compared > 0, "nothing was compared");

        // Well under one call in fifty across the sample. If a dropped tool ever
        // becomes common this fails, which is the point.
        var share = (double)unported / (unported + compared);
        Assert.True(share < 0.02,
            $"dropped tools account for {share:P1} of recorded calls ({unported} of "
            + $"{unported + compared}), which is too many to drop without replacing");
    }

    /// <summary>
    /// The reader finds turns in a real trace. If it silently read none, every
    /// comparison above would compare an empty list against an empty list.
    /// </summary>
    [Fact]
    public void TheReader_FindsTurnsInARealTrace()
    {
        var withTurns = Traces()
            .Take(40)
            .Select(RecordedTrace.ReadTurns)
            .Count(turns => turns.Count > 0);

        Assert.True(withTurns > 0, "no sampled trace yielded a single assistant turn");
    }
}
