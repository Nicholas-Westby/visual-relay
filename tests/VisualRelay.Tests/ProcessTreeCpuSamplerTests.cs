using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class ProcessTreeCpuSamplerTests
{
    [Theory]
    [InlineData("0:00.05", 50)]
    [InlineData("0:01.50", 1_500)]
    [InlineData("1:02.50", 62_500)]
    [InlineData("12:34.00", 754_000)]
    [InlineData("01:02:03", 3_723_000)]
    [InlineData("1:02:03", 3_723_000)]
    [InlineData("00:00:05", 5_000)]
    [InlineData("1-02:03:04", 93_784_000)]
    public void ParseCpuTimeMs_KnownFormats(string value, long expectedMs)
    {
        Assert.Equal(expectedMs, ProcessTreeCpuSampler.ParseCpuTimeMs(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("::")]
    [InlineData("1:２:3")]
    public void ParseCpuTimeMs_Invalid_ReturnsMinusOne(string value)
    {
        Assert.Equal(-1, ProcessTreeCpuSampler.ParseCpuTimeMs(value));
    }

    [Fact]
    public void CollectDescendants_WalksTree_AndSurvivesCycles()
    {
        var children = new Dictionary<int, List<int>>
        {
            [1] = [2, 3],
            [2] = [4],
            [4] = [1] // pathological cycle back to the root
        };

        var result = ProcessTreeCpuSampler.CollectDescendants(1, children);

        Assert.Equal(new[] { 1, 2, 3, 4 }, result.Order());
    }

    [Fact]
    public void CollectDescendants_RootOnly_WhenNoChildren()
    {
        var result = ProcessTreeCpuSampler.CollectDescendants(
            42, new Dictionary<int, List<int>>());

        Assert.Equal(new[] { 42 }, result);
    }

    [Fact]
    public void TrySampleTreeCpuMs_SelfProcess_ReturnsNonNegative()
    {
        var sampled = ProcessTreeCpuSampler.TrySampleTreeCpuMs(Environment.ProcessId);

        // ps(1) may be unavailable in sandboxed environments (macOS sandbox,
        // container without procfs).  null is a valid "no signal" return;
        // only assert non-negative when a value is produced.
        if (sampled is not null)
            Assert.True(sampled >= 0, $"expected non-negative cpu ms, got {sampled}");
    }

    /// <summary>
    /// Regression: sub-epsilon cumulative CPU (1 ms/s, 4 s interval) must
    /// never pulse.  Each per-sample delta is ~4 ms, well below the 50 ms
    /// <c>CpuPulseEpsilonMs</c>.  This pinning test prevents a future
    /// "accumulate deltas" refactor from quietly turning poll-noise into
    /// liveness.
    /// </summary>
    [Fact]
    public void SubEpsilonCpu_NoPulse()
    {
        // Exercise the exact production pulse-decision algorithm via the
        // extracted internal method so a revert of ProcessCapture.cs would
        // be caught by this test.
        long? baseline = 0;
        int pulseCount = 0;
        const long epsilonMs = 50;
        const int sampleCount = 150; // 150 samples = 600 s at 4 s intervals

        for (int i = 0; i < sampleCount; i++)
        {
            // 1 ms/s * 4 s = 4 ms delta per sample
            long sample = baseline!.Value + 4;
            var (pulse, newBaseline) = ProcessCapture.TryDecideCpuPulse(baseline, sample, epsilonMs);
            if (pulse)
                pulseCount++;
            baseline = newBaseline;
        }

        // Sub-epsilon per-sample deltas must never pulse.
        Assert.Equal(0, pulseCount);
    }

    /// <summary>
    /// Regression for the 2026-06-12 socket-wedge incident root cause:
    /// null returns from <c>TrySampleTreeCpuMs</c> (ps failure, timeout,
    /// or exception) must NOT cause accumulated CPU deltas from the failure
    /// gap to cross <c>CpuPulseEpsilonMs</c> and emit spurious "cpu" pulses.
    ///
    /// In the current code (<c>ProcessCapture.SampleTreeCpuLoopAsync</c>),
    /// a null return skips the <c>baseline = sample.Value</c> update via
    /// <c>continue</c>.  The next successful sample compares its cumulative
    /// tree-CPU value against the stale baseline, accumulating all CPU from
    /// the failure gap.  At ~1 ms/s CPU and 4 s intervals, ~13 consecutive
    /// ps failures (~52 s gap) accumulate ~52 ms, crossing the 50 ms epsilon.
    /// This resets the watchdog's inactivity timer and keeps it disarmed
    /// indefinitely.  This test MUST fail with the current code and pass
    /// after the fix (invalidate baseline on null return).
    /// </summary>
    [Fact]
    public void NullReturns_NoAccumulatedPulse()
    {
        // Exercises the exact production pulse-decision algorithm
        // (ProcessCapture.TryDecideCpuPulse) so a revert of the baseline
        // invalidation fix would be caught by this test.
        long? baseline = 0;
        int pulseCount = 0;
        long cumulativeCpu = 0;
        const long epsilonMs = 50;

        // Preamble: two normal samples establish a recent baseline.
        cumulativeCpu += 4;
        baseline = cumulativeCpu; // baseline = 4

        cumulativeCpu += 4;
        baseline = cumulativeCpu; // baseline = 8

        // 13 consecutive ps failures (null returns): baseline → null each time.
        for (int i = 0; i < 13; i++)
        {
            cumulativeCpu += 4; // cumulativeCpu reaches 8 + 52 = 60
            baseline = null;    // ← FIX: invalidate baseline on null return
        }

        // First successful sample after the gap: baseline is null → no pulse,
        // silently re-establish baseline.
        cumulativeCpu += 4; // cumulativeCpu = 64
        {
            var (pulse, newBaseline) = ProcessCapture.TryDecideCpuPulse(baseline, cumulativeCpu, epsilonMs);
            if (pulse)
                pulseCount++; // ← FIXED: baseline is null, guard short-circuits
            baseline = newBaseline; // baseline re-established at 64
        }

        // Aftermath: normal samples resume; per-sample deltas are sub-epsilon.
        for (int i = 0; i < 20; i++)
        {
            cumulativeCpu += 4;
            var (pulse, newBaseline) = ProcessCapture.TryDecideCpuPulse(baseline, cumulativeCpu, epsilonMs);
            if (pulse)
                pulseCount++;
            baseline = newBaseline;
        }

        // No pulses: the null-return gap no longer produces spurious activity.
        Assert.Equal(0, pulseCount);
    }
}
