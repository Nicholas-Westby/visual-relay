using VisualRelay.Core.Agent;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the watchdog rebuilt on direct signals. The load-bearing assertion is
/// the last one in the first section: an event that is not model output must not
/// reset the output-silence clock, while a content delta must. Losing that
/// distinction is exactly what the CPU sampler did, and it is how a
/// request that burned CPU while producing nothing survived to the ceiling.
/// </summary>
public sealed class AgentWatchdogTests
{
    private static readonly TimeSpan FirstOutput = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan Inactivity = TimeSpan.FromSeconds(600);
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(3600);
    private static readonly TimeSpan OutputSilence = TimeSpan.FromSeconds(300);

    private static (AgentWatchdog Watchdog, ManualTimeProvider Clock) Build(
        TimeSpan? outputSilence = null)
    {
        var clock = new ManualTimeProvider();
        var watchdog = new AgentWatchdog(
            FirstOutput, Inactivity, Ceiling, outputSilence ?? OutputSilence, clock);
        watchdog.Start();
        return (watchdog, clock);
    }

    private static AgentEvent Event(AgentEventKind kind, DateTimeOffset at, string? text = null) =>
        new(kind, at, 1, Text: text);

    /// <summary>A quiet, healthy start does not fire.</summary>
    [Fact]
    public void AHealthyStart_DoesNotFire()
    {
        var (watchdog, clock) = Build();

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(AgentWatchdogOutcome.Disarmed, watchdog.Evaluate().Outcome);
    }

    /// <summary>Nothing at all within the first-output budget is a stall.</summary>
    [Fact]
    public void NoFirstOutput_IsAStall()
    {
        var (watchdog, clock) = Build();

        clock.Advance(FirstOutput + TimeSpan.FromSeconds(1));

        var (outcome, kill) = watchdog.Evaluate();
        Assert.Equal(AgentWatchdogOutcome.FiredStall, outcome);
        Assert.Equal("stall", kill!.Reason);
    }

    /// <summary>A content delta resets the output clock.</summary>
    [Fact]
    public void AContentDelta_ResetsTheOutputClock()
    {
        var (watchdog, clock) = Build();

        clock.Advance(TimeSpan.FromSeconds(200));
        watchdog.Publish(Event(AgentEventKind.TokenDelta, clock.GetUtcNow(), "hello"));
        clock.Advance(TimeSpan.FromSeconds(200));

        Assert.Equal(AgentWatchdogOutcome.Disarmed, watchdog.Evaluate().Outcome);
    }

    /// <summary>
    /// The assertion this rewrite exists for: activity that is not model output
    /// keeps the stage alive without resetting the output clock, so a stage that
    /// is busy producing nothing is still cut.
    /// </summary>
    [Fact]
    public void NonOutputActivity_DoesNotResetTheOutputClock()
    {
        var (watchdog, clock) = Build();

        // One real delta, so the output-silence gate is armed.
        watchdog.Publish(Event(AgentEventKind.TokenDelta, clock.GetUtcNow(), "start"));

        // Then a long run of activity that is not model output. Under the old CPU
        // sampler every one of these looked identical to progress.
        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(40));
            watchdog.Publish(Event(AgentEventKind.ToolCallFinished, clock.GetUtcNow()));
        }

        var (outcome, kill) = watchdog.Evaluate();
        Assert.Equal(AgentWatchdogOutcome.FiredOutputSilence, outcome);
        Assert.Equal("output_silence_ceiling", kill!.Reason);
        Assert.True(AgentWatchdog.IsHardAbort(outcome));
    }

    /// <summary>Reasoning counts as output: the model is producing, just not prose.</summary>
    [Fact]
    public void ReasoningCountsAsOutput()
    {
        var (watchdog, clock) = Build();
        watchdog.Publish(Event(AgentEventKind.TokenDelta, clock.GetUtcNow(), "start"));

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(40));
            watchdog.Publish(Event(AgentEventKind.ReasoningDelta, clock.GetUtcNow(), "thinking"));
        }

        Assert.Equal(AgentWatchdogOutcome.Disarmed, watchdog.Evaluate().Outcome);
    }

    /// <summary>The absolute ceiling outranks everything else.</summary>
    [Fact]
    public void TheAbsoluteCeiling_OutranksEverything()
    {
        var (watchdog, clock) = Build();

        // Keep the stage noisy so nothing else could fire.
        for (var i = 0; i < 200; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            watchdog.Publish(Event(AgentEventKind.TokenDelta, clock.GetUtcNow(), "x"));
        }

        var (outcome, kill) = watchdog.Evaluate();
        Assert.Equal(AgentWatchdogOutcome.FiredAbsoluteCeiling, outcome);
        Assert.Equal("absolute_ceiling", kill!.Reason);
    }

    /// <summary>
    /// A stall escalates a tier; the other three outcomes are host conditions a
    /// dearer model would hit just the same.
    /// </summary>
    [Fact]
    public void OnlyAPlainStall_Escalates()
    {
        Assert.False(AgentWatchdog.IsHardAbort(AgentWatchdogOutcome.FiredStall));
        Assert.True(AgentWatchdog.IsHardAbort(AgentWatchdogOutcome.FiredAbsoluteCeiling));
        Assert.True(AgentWatchdog.IsHardAbort(AgentWatchdogOutcome.FiredOutputSilence));
        Assert.True(AgentWatchdog.IsHardAbort(AgentWatchdogOutcome.FiredSocketWedge));
        Assert.False(AgentWatchdog.IsHardAbort(AgentWatchdogOutcome.Disarmed));
    }

    /// <summary>
    /// Setting the output-silence budget to zero disables the gate, which is how
    /// this repo's own config left it, so it never once fired in production.
    /// </summary>
    [Fact]
    public void AZeroOutputSilenceBudget_DisablesTheGate()
    {
        var (watchdog, clock) = Build(outputSilence: TimeSpan.Zero);
        watchdog.Publish(Event(AgentEventKind.TokenDelta, clock.GetUtcNow(), "start"));

        for (var i = 0; i < 10; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(40));
            watchdog.Publish(Event(AgentEventKind.ToolCallFinished, clock.GetUtcNow()));
        }

        Assert.Equal(AgentWatchdogOutcome.Disarmed, watchdog.Evaluate().Outcome);
    }

    /// <summary>The kill signature names the last signal seen, for the autopsy.</summary>
    [Fact]
    public void TheKillSignature_NamesTheLastSignal()
    {
        var (watchdog, clock) = Build();
        // Not a tool call: one still running is no stall (AgentWatchdogToolCallTests).
        watchdog.Publish(Event(AgentEventKind.Retry, clock.GetUtcNow()));

        clock.Advance(FirstOutput + TimeSpan.FromSeconds(1));

        var (_, kill) = watchdog.Evaluate();
        Assert.Equal("Retry", kill!.LastSignal);
        Assert.True(kill.SilenceMs >= FirstOutput.TotalMilliseconds);
    }

    /// <summary>An unstarted watchdog never fires, whatever the clock does.</summary>
    [Fact]
    public void AnUnstartedWatchdog_NeverFires()
    {
        var clock = new ManualTimeProvider();
        var watchdog = new AgentWatchdog(FirstOutput, Inactivity, Ceiling, OutputSilence, clock);

        clock.Advance(TimeSpan.FromHours(10));

        Assert.Equal(AgentWatchdogOutcome.Disarmed, watchdog.Evaluate().Outcome);
    }

    /// <summary>
    /// A slow but healthy stage, producing output every few minutes for the best
    /// part of an hour, is never killed. Length alone is not a wedge.
    /// </summary>
    [Fact]
    public void ASlowButHealthyStage_IsNeverKilled()
    {
        var clock = new ManualTimeProvider();
        var watchdog = new AgentWatchdog(
            FirstOutput, Inactivity, TimeSpan.FromHours(4), OutputSilence, clock);
        watchdog.Start();

        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            watchdog.Publish(Event(AgentEventKind.TokenDelta, clock.GetUtcNow(), "progress"));
            Assert.Equal(AgentWatchdogOutcome.Disarmed, watchdog.Evaluate().Outcome);
        }
    }
}
