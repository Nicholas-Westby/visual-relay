namespace VisualRelay.Core.Agent;

/// <summary>
/// Watches for a model failing the same way over and over. Fired 95 times across
/// the recorded corpus.
/// <para>
/// It intervenes by telling the model to change approach; it never aborts the
/// run. The previous implementation carried two landmines that ended the whole
/// run on a second repair failure, which took the decision away from the layer
/// that has the context to make it. Escalation belongs to the driver.
/// </para>
/// </summary>
/// <param name="options">Where the limit comes from; defaults to the measured set.</param>
public sealed class ConsecutiveErrorGuardrail(AgentResilienceOptions? options = null)
{
    private readonly AgentResilienceOptions _options = options ?? AgentResilienceOptions.Default;

    /// <summary>How many errors have arrived back to back.</summary>
    public int ConsecutiveErrors { get; private set; }

    /// <summary>How many times this guardrail has intervened during the stage.</summary>
    public int Interventions { get; private set; }

    /// <summary>Records one tool outcome.</summary>
    /// <param name="isError">Whether the tool call failed.</param>
    public void Record(bool isError)
    {
        if (isError) ConsecutiveErrors++;
        else ConsecutiveErrors = 0;
    }

    /// <summary>
    /// The nudge to hand the model, or <c>null</c> while things are fine.
    /// Asking for it counts as an intervention and resets the streak, so the
    /// model gets a clear run at the new approach before being told again.
    /// </summary>
    /// <returns>The intervention text, or <c>null</c>.</returns>
    public string? TryIntervene()
    {
        if (ConsecutiveErrors < _options.ConsecutiveErrorLimit) return null;

        Interventions++;
        ConsecutiveErrors = 0;
        return $"{_options.ConsecutiveErrorLimit} tool calls in a row have failed. "
            + "Stop and reconsider: re-read the error messages, check the paths and arguments "
            + "you are passing, and try a different approach rather than a variation of the "
            + "same one.";
    }
}
