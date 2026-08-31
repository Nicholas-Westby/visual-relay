namespace VisualRelay.Core.Agent;

/// <summary>What the loop should do about a repeated tool call.</summary>
public enum StormVerdict
{
    /// <summary>Run it. Nothing unusual.</summary>
    Allow,

    /// <summary>
    /// Run it, but tell the model it is repeating itself. Warning before
    /// refusing matters: re-running one test command IS how you check for a
    /// flaky test, and the previous implementation silently suppressed exactly
    /// that, 41 times across the recorded corpus.
    /// </summary>
    Warn,

    /// <summary>
    /// Refuse it. The model is looping on an identical call and making no
    /// progress; running it again cannot produce a different answer.
    /// </summary>
    Suppress,
}

/// <summary>
/// Detects a model repeating the same tool call with the same arguments.
/// <para>
/// Both the window and the threshold are configurable, which the previous
/// implementation's hardcoded 6 and 3 were not. It warns first and only
/// suppresses once the repetition is past argument, so a deliberate re-run
/// stays possible.
/// </para>
/// </summary>
/// <param name="options">Window and threshold; defaults to the measured set.</param>
public sealed class RepeatCallStormBreaker(AgentResilienceOptions? options = null)
{
    private readonly AgentResilienceOptions _options = options ?? AgentResilienceOptions.Default;
    private readonly List<string> _recent = [];

    /// <summary>How many calls have been suppressed during this stage.</summary>
    public int SuppressedCount { get; private set; }

    /// <summary>
    /// Records a call and returns what to do about it.
    /// </summary>
    /// <param name="toolName">The tool being called.</param>
    /// <param name="arguments">Its raw argument JSON, compared verbatim.</param>
    /// <returns>The verdict for this call.</returns>
    public StormVerdict Observe(string toolName, string arguments)
    {
        var signature = toolName + " " + arguments;
        _recent.Add(signature);
        if (_recent.Count > _options.StormWindow) _recent.RemoveAt(0);

        var repeats = _recent.Count(s => string.Equals(s, signature, StringComparison.Ordinal));

        if (repeats < _options.StormThreshold) return StormVerdict.Allow;

        // At the threshold, say so. Only past it does refusing become right.
        if (repeats == _options.StormThreshold) return StormVerdict.Warn;

        SuppressedCount++;
        return StormVerdict.Suppress;
    }

    /// <summary>The message the model sees for a warned or suppressed call.</summary>
    /// <param name="verdict">The verdict that was reached.</param>
    /// <param name="toolName">The tool being repeated.</param>
    /// <returns>Text telling the model what to do differently.</returns>
    public string Explain(StormVerdict verdict, string toolName) => verdict switch
    {
        StormVerdict.Warn =>
            $"You have now called {toolName} with identical arguments "
            + $"{_options.StormThreshold} times. If you are checking for a flaky result that is "
            + "fine; otherwise change the arguments or try a different approach.",
        StormVerdict.Suppress =>
            $"Refused: {toolName} was called with identical arguments more than "
            + $"{_options.StormThreshold} times in the last {_options.StormWindow} calls. "
            + "The result will not change. Do something different.",
        _ => string.Empty,
    };
}
