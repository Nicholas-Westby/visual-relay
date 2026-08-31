using VisualRelay.Core.Agent;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the three resilience mechanisms the recorded corpus justifies: the
/// repeat-call storm breaker (41 suppressions), the consecutive-error guardrail
/// (95 interventions) and unknown-tool recovery (13 firings). The scavenger and
/// turn-drop machinery are deliberately absent: both fired zero times across
/// 1109 stages, so there is nothing here to test.
/// </summary>
public sealed class AgentResilienceTests
{
    // ── Repeat-call storm breaker ─────────────────────────────────────────

    /// <summary>Distinct calls never trip the detector.</summary>
    [Fact]
    public void DistinctCalls_AreAlwaysAllowed()
    {
        var breaker = new RepeatCallStormBreaker();

        Assert.Equal(StormVerdict.Allow, breaker.Observe("read_file", """{"path":"a"}"""));
        Assert.Equal(StormVerdict.Allow, breaker.Observe("read_file", """{"path":"b"}"""));
        Assert.Equal(StormVerdict.Allow, breaker.Observe("grep", """{"pattern":"x"}"""));
        Assert.Equal(0, breaker.SuppressedCount);
    }

    /// <summary>
    /// The third identical call warns rather than refusing. That is the whole
    /// point of the rewrite: re-running one test command is how a flaky test is
    /// diagnosed, and the previous hardcoded detector silently suppressed it.
    /// </summary>
    [Fact]
    public void ThirdIdenticalCall_WarnsButStillRuns()
    {
        var breaker = new RepeatCallStormBreaker();
        const string args = """{"command":["sh","run-tests.sh"]}""";

        Assert.Equal(StormVerdict.Allow, breaker.Observe("run_command", args));
        Assert.Equal(StormVerdict.Allow, breaker.Observe("run_command", args));

        Assert.Equal(StormVerdict.Warn, breaker.Observe("run_command", args));
        Assert.Equal(0, breaker.SuppressedCount);
    }

    /// <summary>Past the threshold the call is refused, and that is counted.</summary>
    [Fact]
    public void FourthIdenticalCall_IsSuppressed()
    {
        var breaker = new RepeatCallStormBreaker();
        const string args = """{"path":"same"}""";

        for (var i = 0; i < 3; i++) breaker.Observe("read_file", args);

        Assert.Equal(StormVerdict.Suppress, breaker.Observe("read_file", args));
        Assert.Equal(1, breaker.SuppressedCount);
    }

    /// <summary>
    /// The threshold is configurable, which the previous 6-and-3 was not. Raising
    /// it lets a stage legitimately re-run one command more often.
    /// </summary>
    [Fact]
    public void RaisingTheThreshold_AllowsMoreRepeats()
    {
        var breaker = new RepeatCallStormBreaker(
            new AgentResilienceOptions(StormWindow: 10, StormThreshold: 5));
        const string args = """{"command":["sh","run-tests.sh"]}""";

        for (var i = 0; i < 4; i++)
            Assert.Equal(StormVerdict.Allow, breaker.Observe("run_command", args));

        Assert.Equal(StormVerdict.Warn, breaker.Observe("run_command", args));
    }

    /// <summary>
    /// Repeats that fall outside the window do not accumulate, so a command
    /// re-run much later in the stage starts from a clean slate.
    /// </summary>
    [Fact]
    public void RepeatsOutsideTheWindow_DoNotAccumulate()
    {
        var breaker = new RepeatCallStormBreaker(
            new AgentResilienceOptions(StormWindow: 3, StormThreshold: 3));

        breaker.Observe("read_file", """{"path":"a"}""");
        breaker.Observe("read_file", """{"path":"b"}""");
        breaker.Observe("read_file", """{"path":"c"}""");
        breaker.Observe("read_file", """{"path":"d"}""");

        Assert.Equal(StormVerdict.Allow, breaker.Observe("read_file", """{"path":"a"}"""));
    }

    /// <summary>Both intervention messages tell the model what to do next.</summary>
    [Fact]
    public void StormMessages_TellTheModelWhatToDo()
    {
        var breaker = new RepeatCallStormBreaker();

        var warn = breaker.Explain(StormVerdict.Warn, "run_command");
        var suppress = breaker.Explain(StormVerdict.Suppress, "run_command");

        Assert.Contains("flaky", warn, StringComparison.Ordinal);
        Assert.Contains("run_command", warn, StringComparison.Ordinal);
        Assert.Contains("Do something different", suppress, StringComparison.Ordinal);
        Assert.Equal(string.Empty, breaker.Explain(StormVerdict.Allow, "run_command"));
    }

    // ── Consecutive-error guardrail ───────────────────────────────────────

    /// <summary>A success anywhere in the run resets the streak.</summary>
    [Fact]
    public void SuccessResetsTheErrorStreak()
    {
        var guardrail = new ConsecutiveErrorGuardrail();

        for (var i = 0; i < 4; i++) guardrail.Record(isError: true);
        guardrail.Record(isError: false);

        Assert.Equal(0, guardrail.ConsecutiveErrors);
        Assert.Null(guardrail.TryIntervene());
    }

    /// <summary>At the limit it intervenes, and the message names a change of approach.</summary>
    [Fact]
    public void AtTheLimit_ItIntervenes()
    {
        var guardrail = new ConsecutiveErrorGuardrail();

        for (var i = 0; i < 5; i++) guardrail.Record(isError: true);
        var message = guardrail.TryIntervene();

        Assert.NotNull(message);
        Assert.Contains("different approach", message!, StringComparison.Ordinal);
        Assert.Equal(1, guardrail.Interventions);
    }

    /// <summary>
    /// Intervening resets the streak, so the model gets a clear run at the new
    /// approach before being told again.
    /// </summary>
    [Fact]
    public void Intervening_ResetsTheStreak()
    {
        var guardrail = new ConsecutiveErrorGuardrail();

        for (var i = 0; i < 5; i++) guardrail.Record(isError: true);
        guardrail.TryIntervene();

        Assert.Equal(0, guardrail.ConsecutiveErrors);
        Assert.Null(guardrail.TryIntervene());
    }

    /// <summary>
    /// The guardrail never aborts the run. It reports; the driver decides. The
    /// previous implementation ended the whole run from this position.
    /// </summary>
    [Fact]
    public void TheGuardrail_NeverAborts()
    {
        var guardrail = new ConsecutiveErrorGuardrail(
            new AgentResilienceOptions(ConsecutiveErrorLimit: 1));

        for (var i = 0; i < 20; i++)
        {
            guardrail.Record(isError: true);
            guardrail.TryIntervene();
        }

        Assert.Equal(20, guardrail.Interventions);
    }

    // ── Unknown-tool recovery ─────────────────────────────────────────────

    /// <summary>A near miss is corrected by name rather than by listing everything.</summary>
    /// <param name="requested">The name the model asked for.</param>
    /// <param name="expected">The name it should be pointed at.</param>
    [Theory]
    [InlineData("read_files", "read_file")]
    [InlineData("list_file", "list_files")]
    [InlineData("viewimage", "view_image")]
    [InlineData("Grep", "grep")]
    public void NearMisses_AreCorrectedByName(string requested, string expected)
    {
        string[] available = ["read_file", "list_files", "view_image", "grep", "write_file"];

        Assert.Equal(expected, UnknownToolRecovery.NearestMatch(requested, available));
        Assert.Contains($"Did you mean '{expected}'", UnknownToolRecovery.Describe(requested, available),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A name resembling nothing gets the full list instead of a misleading
    /// suggestion, so the model is not steered at an unrelated tool.
    /// </summary>
    [Fact]
    public void AnUnrelatedName_GetsTheListNotAGuess()
    {
        string[] available = ["read_file", "list_files", "grep"];

        Assert.Null(UnknownToolRecovery.NearestMatch("send_email", available));

        var message = UnknownToolRecovery.Describe("send_email", available);
        Assert.Contains("Available tools:", message, StringComparison.Ordinal);
        Assert.Contains("read_file", message, StringComparison.Ordinal);
    }

    /// <summary>An empty name or an empty tool set does not throw.</summary>
    [Fact]
    public void EmptyInputs_AreHandled()
    {
        Assert.Null(UnknownToolRecovery.NearestMatch("", ["read_file"]));
        Assert.Null(UnknownToolRecovery.NearestMatch("read_file", []));
    }
}
