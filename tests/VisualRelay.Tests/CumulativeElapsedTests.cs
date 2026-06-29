using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the reusable <see cref="CumulativeElapsed"/> accumulator that
/// underpins both the retried-stage card timer and the overall task active-time.
/// A retry/escalation re-opens a segment WITHOUT discarding the time already
/// banked, so the total sums across attempts instead of resetting per attempt.
/// </summary>
public sealed class CumulativeElapsedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Total_OfFreshAccumulator_IsZero()
    {
        var elapsed = new CumulativeElapsed();
        Assert.Equal(TimeSpan.Zero, elapsed.Total(T0));
    }

    [Fact]
    public void OpenSegment_Total_IsLiveWallClock()
    {
        var elapsed = new CumulativeElapsed();
        elapsed.StartSegment(T0);
        Assert.Equal(TimeSpan.FromSeconds(30), elapsed.Total(T0 + TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void CompleteSegment_BanksReportedDuration_AndStopsTicking()
    {
        var elapsed = new CumulativeElapsed();
        elapsed.StartSegment(T0);
        elapsed.CompleteSegment(TimeSpan.FromSeconds(120));

        // No open segment: the total is the banked reported duration regardless of "now".
        Assert.Equal(TimeSpan.FromSeconds(120), elapsed.Total(T0 + TimeSpan.FromHours(1)));
        Assert.False(elapsed.IsRunning);
    }

    [Fact]
    public void Retry_AccumulatesAcrossAttempts_ReportedPlusLive()
    {
        var elapsed = new CumulativeElapsed();
        // Attempt 1: reported 300 s.
        elapsed.StartSegment(T0);
        elapsed.CompleteSegment(TimeSpan.FromSeconds(300));
        // Attempt 2: started, still running 100 s in.
        elapsed.StartSegment(T0 + TimeSpan.FromSeconds(500));

        // Cumulative = banked 300 s + live 100 s = 400 s — NOT the 100 s of attempt 2 alone.
        Assert.Equal(TimeSpan.FromSeconds(400), elapsed.Total(T0 + TimeSpan.FromSeconds(600)));
    }

    [Fact]
    public void IdleGapBetweenSegments_IsExcluded()
    {
        var elapsed = new CumulativeElapsed();
        elapsed.StartSegment(T0);
        elapsed.CompleteSegment(TimeSpan.FromSeconds(60));
        // A long idle gap with NO open segment (queue-wait while another task runs).
        // Then a second segment of 40 s.
        elapsed.StartSegment(T0 + TimeSpan.FromHours(1));
        elapsed.CompleteSegment(TimeSpan.FromSeconds(40));

        // Only the two active segments count; the hour of idle is excluded.
        Assert.Equal(TimeSpan.FromSeconds(100), elapsed.Total(T0 + TimeSpan.FromHours(2)));
    }

    [Fact]
    public void StopSegment_DropsLiveWithoutBanking()
    {
        var elapsed = new CumulativeElapsed();
        elapsed.CompleteSegment(TimeSpan.FromSeconds(50)); // bank 50 s
        elapsed.StartSegment(T0);
        elapsed.StopSegment(); // abandon the live segment (e.g. flagged) without banking it

        Assert.Equal(TimeSpan.FromSeconds(50), elapsed.Total(T0 + TimeSpan.FromSeconds(999)));
        Assert.False(elapsed.IsRunning);
    }

    [Fact]
    public void CompleteSegment_ByEndTimestamp_BanksWallClock_WhenNoReportedDuration()
    {
        var elapsed = new CumulativeElapsed();
        elapsed.StartSegment(T0);
        elapsed.CompleteSegment(T0 + TimeSpan.FromSeconds(45)); // fallback: measure by timestamps

        Assert.Equal(TimeSpan.FromSeconds(45), elapsed.Total(T0 + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Reset_ClearsBankedAndLive()
    {
        var elapsed = new CumulativeElapsed();
        elapsed.CompleteSegment(TimeSpan.FromSeconds(120));
        elapsed.StartSegment(T0);
        elapsed.Reset();

        Assert.Equal(TimeSpan.Zero, elapsed.Total(T0 + TimeSpan.FromSeconds(60)));
        Assert.False(elapsed.IsRunning);
    }
}
