using VisualRelay.Core.Agent;

namespace VisualRelay.Tests;

/// <summary>
/// A tool call in flight is not a stall, and its time is not the model's silence. Measured
/// on the Windows arm with crawl: the research agent's single-threaded <c>make</c>, run with
/// <c>timeout_seconds</c> 600, sent no event for 600 s, and the stage died at the 600000 ms
/// inactivity window instead of handing the command's own timeout back to the model. A
/// command is bounded by that timeout and a stage by its ceiling, so neither clock guesses.
/// </summary>
public sealed class AgentWatchdogToolCallTests
{
    private static readonly TimeSpan FirstOutput = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan Inactivity = TimeSpan.FromSeconds(600);
    private static readonly TimeSpan Ceiling = TimeSpan.FromHours(2);
    private static readonly TimeSpan OutputSilence = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan LongerThanEveryStallWindow = TimeSpan.FromMinutes(25);

    private static (AgentWatchdog Watchdog, ManualTimeProvider Clock) Build(TimeSpan outputSilence)
    {
        var clock = new ManualTimeProvider();
        var watchdog = new AgentWatchdog(FirstOutput, Inactivity, Ceiling, outputSilence, clock);
        watchdog.Start();
        return (watchdog, clock);
    }

    private static void Publish(AgentWatchdog watchdog, ManualTimeProvider clock, AgentEventKind kind) =>
        watchdog.Publish(new AgentEvent(kind, clock.GetUtcNow(), 1, Text: "x"));

    /// <summary>A turn that spoke, then asked for a tool one second later.</summary>
    private static void ModelStartsATool(AgentWatchdog watchdog, ManualTimeProvider clock)
    {
        Publish(watchdog, clock, AgentEventKind.TurnStarted);
        Publish(watchdog, clock, AgentEventKind.TokenDelta);
        Publish(watchdog, clock, AgentEventKind.TurnFinished);
        clock.Advance(TimeSpan.FromSeconds(1));
        Publish(watchdog, clock, AgentEventKind.ToolCallStarted);
    }

    [Fact]
    public void AToolCallLongerThanTheInactivityWindow_IsNotAStall()
    {
        var (watchdog, clock) = Build(outputSilence: TimeSpan.Zero);
        ModelStartsATool(watchdog, clock);

        clock.Advance(LongerThanEveryStallWindow);

        Assert.Equal(AgentWatchdogOutcome.Disarmed, watchdog.Evaluate().Outcome);
    }

    [Fact]
    public void ALongToolCall_IsNotModelSilence()
    {
        var (watchdog, clock) = Build(OutputSilence);
        ModelStartsATool(watchdog, clock);

        clock.Advance(LongerThanEveryStallWindow);
        var whileRunning = watchdog.Evaluate().Outcome;
        Publish(watchdog, clock, AgentEventKind.ToolCallFinished);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(AgentWatchdogOutcome.Disarmed, whileRunning);
        Assert.Equal(AgentWatchdogOutcome.Disarmed, watchdog.Evaluate().Outcome);
    }

    [Fact]
    public void TheRequestAfterALongToolCall_IsNotAWedgeTheMomentItStarts()
    {
        var (watchdog, clock) = Build(outputSilence: TimeSpan.Zero);
        ModelStartsATool(watchdog, clock);
        clock.Advance(LongerThanEveryStallWindow);
        Publish(watchdog, clock, AgentEventKind.ToolCallFinished);

        Publish(watchdog, clock, AgentEventKind.TurnStarted);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(AgentWatchdogOutcome.Disarmed, watchdog.Evaluate().Outcome);
    }

    [Fact]
    public void ARequestThatHangsAfterALongToolCall_IsStillAWedge()
    {
        var (watchdog, clock) = Build(outputSilence: TimeSpan.Zero);
        ModelStartsATool(watchdog, clock);
        clock.Advance(LongerThanEveryStallWindow);
        Publish(watchdog, clock, AgentEventKind.ToolCallFinished);
        Publish(watchdog, clock, AgentEventKind.TurnStarted);

        // Retries keep the loop active, so what fires is the wedge, not a stall.
        for (var i = 0; i < 11; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            Publish(watchdog, clock, AgentEventKind.Retry);
        }

        Assert.Equal(AgentWatchdogOutcome.FiredSocketWedge, watchdog.Evaluate().Outcome);
    }

    [Fact]
    public void QuietAfterALongToolCall_IsStillAStall()
    {
        var (watchdog, clock) = Build(outputSilence: TimeSpan.Zero);
        ModelStartsATool(watchdog, clock);
        clock.Advance(LongerThanEveryStallWindow);
        Publish(watchdog, clock, AgentEventKind.ToolCallFinished);

        clock.Advance(Inactivity + TimeSpan.FromSeconds(1));

        Assert.Equal(AgentWatchdogOutcome.FiredStall, watchdog.Evaluate().Outcome);
    }

    [Fact]
    public void AToolCall_StillRunsIntoTheCeiling()
    {
        var (watchdog, clock) = Build(OutputSilence);
        ModelStartsATool(watchdog, clock);

        clock.Advance(Ceiling);

        Assert.Equal(AgentWatchdogOutcome.FiredAbsoluteCeiling, watchdog.Evaluate().Outcome);
    }
}
