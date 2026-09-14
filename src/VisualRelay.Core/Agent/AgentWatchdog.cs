using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

/// <summary>Why the watchdog fired, or that it did not.</summary>
public enum AgentWatchdogOutcome
{
    /// <summary>Nothing wrong.</summary>
    Disarmed,

    /// <summary>
    /// Nothing has happened for the inactivity budget. The one outcome that
    /// escalates a tier, because a stall can be the model rather than the host.
    /// </summary>
    FiredStall,

    /// <summary>The stage exceeded its absolute wall clock. Never escalates.</summary>
    FiredAbsoluteCeiling,

    /// <summary>
    /// A request is in flight and has produced no model output for the
    /// inactivity budget, while other activity continues. Never escalates.
    /// </summary>
    FiredSocketWedge,

    /// <summary>No model output at all for the output-silence budget. Never escalates.</summary>
    FiredOutputSilence,
}

/// <summary>
/// Decides whether a stage has wedged, from the loop's own event stream.
/// <para>
/// It replaces sampling the process tree's CPU and scraping the OS TCP table —
/// two stand-ins for "is something happening" that were needed only because the
/// agent ran behind a process boundary. In process, the signals are direct: when
/// the last token arrived, when the last tool call started and returned, and
/// whether a request is outstanding.
/// </para>
/// <para>
/// Two clocks, deliberately separate. Any event advances the activity clock;
/// only model output advances the output clock. Collapsing them is what the CPU
/// sampler did, and it is why a request that burned CPU while producing nothing
/// survived to the absolute ceiling instead of being cut in seconds.
/// </para>
/// <para>
/// A tool call in flight stops both: it is not a stall, and the model is not silent
/// while it waits for the result. The tool's own timeout bounds it, and the ceiling
/// still bounds the stage.
/// </para>
/// </summary>
/// <param name="firstOutputTimeout">How long to wait for the first model output.</param>
/// <param name="inactivityTimeout">How long any silence may last.</param>
/// <param name="absoluteCeiling">The whole stage's wall clock.</param>
/// <param name="outputSilenceTimeout">
/// How long the model may produce nothing while other activity continues. Zero
/// disables the gate, which is how it was left in this repo's own config, so it
/// never once fired in production.
/// </param>
/// <param name="timeProvider">Clock, for virtual-time tests.</param>
public sealed class AgentWatchdog(
    TimeSpan firstOutputTimeout,
    TimeSpan inactivityTimeout,
    TimeSpan absoluteCeiling,
    TimeSpan outputSilenceTimeout = default,
    TimeProvider? timeProvider = null) : IAgentEventSink
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();

    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _lastOutput;
    private bool _started;
    private bool _sawOutput;
    private bool _requestInFlight;
    private DateTimeOffset? _toolStartedAt;
    private string _lastSignal = "none";

    /// <summary>Starts the clocks. Call once, as the stage begins.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _startedAt = _lastActivity = _lastOutput = _timeProvider.GetUtcNow();
            _started = true;
        }
    }

    /// <inheritdoc />
    public void Publish(AgentEvent agentEvent)
    {
        lock (_gate)
        {
            if (!_started) return;

            var now = _timeProvider.GetUtcNow();
            _lastActivity = now;
            _lastSignal = agentEvent.Kind.ToString();

            // ONLY model output moves the output clock. A tool finishing, a
            // usage report or a retry all prove the loop is alive without
            // proving the model is saying anything.
            if (agentEvent.IsModelOutput)
            {
                _lastOutput = now;
                _sawOutput = true;
            }

            // The model cannot speak while a tool runs, so the tool's time is taken off
            // its silence: the output clock moves forward by exactly that long.
            switch (agentEvent.Kind)
            {
                case AgentEventKind.ToolCallStarted:
                    _toolStartedAt = now;
                    break;
                case AgentEventKind.ToolCallFinished when _toolStartedAt is { } toolStarted:
                    _lastOutput += now - toolStarted;
                    _toolStartedAt = null;
                    break;
            }

            _requestInFlight = agentEvent.Kind switch
            {
                AgentEventKind.TurnStarted => true,
                AgentEventKind.TurnFinished => false,
                _ => _requestInFlight,
            };
        }
    }

    /// <summary>
    /// Evaluates the clocks now.
    /// <para>
    /// Priority is fixed and matters: the absolute ceiling outranks everything,
    /// then output silence, then a plain stall, then a wedge. Only the plain
    /// stall escalates a tier; the other three are host problems that a dearer
    /// model cannot fix.
    /// </para>
    /// </summary>
    /// <returns>The outcome, and the kill signature when one fired.</returns>
    public (AgentWatchdogOutcome Outcome, KillSignature? Kill) Evaluate()
    {
        lock (_gate)
        {
            if (!_started) return (AgentWatchdogOutcome.Disarmed, null);

            var now = _timeProvider.GetUtcNow();
            var elapsed = now - _startedAt;
            var sinceActivity = now - _lastActivity;
            // A running tool publishes nothing until it returns, yet it is bounded by its own
            // timeout and the stage by the ceiling below: measured with crawl, a 600 s build
            // was killed as a stall while its command still had time. Its time is not silence.
            var toolRunning = _toolStartedAt is not null;
            var sinceOutput = (_toolStartedAt ?? now) - _lastOutput;

            if (absoluteCeiling > TimeSpan.Zero && elapsed >= absoluteCeiling)
                return Fire(AgentWatchdogOutcome.FiredAbsoluteCeiling, "absolute_ceiling", elapsed);

            if (outputSilenceTimeout > TimeSpan.Zero && _sawOutput && sinceOutput >= outputSilenceTimeout)
                return Fire(AgentWatchdogOutcome.FiredOutputSilence, "output_silence_ceiling", sinceOutput);

            // Before any output has arrived the first-output budget applies;
            // after it, the ordinary inactivity budget does.
            var stallBudget = _sawOutput ? inactivityTimeout : firstOutputTimeout;
            if (!toolRunning && stallBudget > TimeSpan.Zero && sinceActivity >= stallBudget)
                return Fire(AgentWatchdogOutcome.FiredStall, "stall", sinceActivity);

            // Activity continues but the model has gone quiet mid-request: the
            // direct-signal equivalent of the old TCP-table wedge.
            if (_requestInFlight && inactivityTimeout > TimeSpan.Zero && sinceOutput >= inactivityTimeout)
                return Fire(AgentWatchdogOutcome.FiredSocketWedge, "socket_wedge", sinceOutput);

            return (AgentWatchdogOutcome.Disarmed, null);
        }
    }

    /// <summary>
    /// Whether an outcome is a hard abort the driver must not escalate around.
    /// A ceiling, an output-silence kill and a wedge are all host conditions; a
    /// dearer tier would hit the same wall.
    /// </summary>
    /// <param name="outcome">The outcome to classify.</param>
    /// <returns>True when the driver must flag rather than retry.</returns>
    public static bool IsHardAbort(AgentWatchdogOutcome outcome) => outcome is
        AgentWatchdogOutcome.FiredAbsoluteCeiling
        or AgentWatchdogOutcome.FiredOutputSilence
        or AgentWatchdogOutcome.FiredSocketWedge;

    private (AgentWatchdogOutcome, KillSignature?) Fire(
        AgentWatchdogOutcome outcome, string reason, TimeSpan silence) =>
        (outcome, new KillSignature(reason, _lastSignal, (long)silence.TotalMilliseconds, null));
}
