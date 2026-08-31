using System.Globalization;
using System.Text.Json;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// The timeout a command tool actually applies, plus the sentences that explain it
/// to the model.
/// <para>Swival clamped every command to a hidden <c>MAX_TIMEOUT = 240</c> seconds
/// with no flag or env var, and then reported <c>command timed out after 240s</c> —
/// a number the model never asked for, so it could not learn to adapt and retried at
/// the same doomed value (378 of 5,750 command calls in this repo's history asked for
/// more than that cap). Here a model-supplied <c>timeout_seconds</c> is honoured
/// verbatim; the ONLY ceiling is <see cref="ToolContext.RemainingStageBudget"/>. When
/// the value IS reduced the result says so, naming what was requested, what was
/// applied, and why — a silent reduction is the bug being fixed.</para>
/// </summary>
/// <param name="Applied">The timeout actually handed to the process.</param>
/// <param name="Requested">What the model asked for, or null when it named none.</param>
/// <param name="Remaining">What was left of the stage budget when the call started.</param>
/// <param name="Reduced">True when <c>Applied</c> is below the value that was asked for.</param>
/// <param name="Defaulted">True when the model named no <c>timeout_seconds</c>.</param>
internal sealed record CommandTimeoutBudget(
    TimeSpan Applied,
    TimeSpan? Requested,
    TimeSpan Remaining,
    bool Reduced,
    bool Defaulted)
{
    /// <summary>The argument through which the model asks for a timeout.</summary>
    public const string ArgumentName = "timeout_seconds";

    /// <summary>
    /// Applied when the model names no <see cref="ArgumentName"/>. A documented,
    /// model-overridable default — not a ceiling: any larger value the model asks
    /// for is honoured up to the remaining stage budget, and this number is always
    /// named in the timeout text so the model can raise it.
    /// </summary>
    public const int DefaultSeconds = 120;

    /// <summary>
    /// Resolves the timeout for one call. Returns the budget, or an error sentence
    /// when the arguments cannot yield one.
    /// </summary>
    /// <param name="arguments">The model's arguments object.</param>
    /// <param name="remaining">What is left of the stage's wall clock.</param>
    /// <returns>Exactly one of the two members is non-null.</returns>
    public static (CommandTimeoutBudget? Budget, string? Error) Resolve(
        JsonElement arguments, TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return (null, "the stage's time budget is exhausted, so no command can be started. "
                + "Nothing about this call can change that: report what you have and stop.");
        }

        TimeSpan? requested = null;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty(ArgumentName, out var element)
            && element.ValueKind != JsonValueKind.Null)
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var seconds))
                return (null, $"{ArgumentName} must be a number of seconds.");

            if (double.IsNaN(seconds) || seconds <= 0)
                return (null, $"{ArgumentName} must be a positive number of seconds.");

            requested = seconds >= TimeSpan.MaxValue.TotalSeconds
                ? TimeSpan.MaxValue
                : TimeSpan.FromSeconds(seconds);
        }

        var wanted = requested ?? TimeSpan.FromSeconds(DefaultSeconds);
        var applied = wanted <= remaining ? wanted : remaining;

        return (new CommandTimeoutBudget(
            applied, requested, remaining, Reduced: applied < wanted, Defaulted: requested is null), null);
    }

    /// <summary>
    /// Why the applied timeout is smaller than the one that was wanted, or null when
    /// nothing was reduced. Naming the requested value, the applied value and the
    /// reason is what lets the model adapt instead of retrying the same number.
    /// </summary>
    public string? ReductionExplanation => !Reduced
        ? null
        : Defaulted
            ? $"no {ArgumentName} was given, so the {DefaultSeconds}s default applied, and it was "
              + $"reduced to {FormatSeconds(Applied)}s because only {FormatSeconds(Remaining)}s remain "
              + "of this stage's budget, which is the only ceiling on a command timeout."
            : $"{ArgumentName}={FormatSeconds(Requested!.Value)} was reduced to {FormatSeconds(Applied)}s "
              + $"because only {FormatSeconds(Remaining)}s remain of this stage's budget, which is the only "
              + $"ceiling on a command timeout. Re-running with a larger {ArgumentName} cannot raise it.";

    /// <summary>
    /// The timeout failure text. It always names the timeout that was ACTUALLY
    /// applied — never a hidden constant — and says where that number came from.
    /// </summary>
    public string TimedOutMessage =>
        $"command timed out after {FormatSeconds(Applied)}s, the timeout that was actually applied. "
        + Provenance;

    private string Provenance => Reduced
        ? ReductionExplanation!
        : Defaulted
            ? $"No {ArgumentName} was given, so the {DefaultSeconds}s default applied; "
              + $"{FormatSeconds(Remaining)}s remain of this stage's budget, so pass a larger "
              + $"{ArgumentName} (up to {FormatSeconds(Remaining)}s) to allow more time."
            : $"That is exactly the {ArgumentName}={FormatSeconds(Requested!.Value)} you asked for — "
              + $"nothing was reduced. {FormatSeconds(Remaining)}s remained of this stage's budget, so a "
              + $"larger {ArgumentName} would have been honoured too.";

    /// <summary>Renders a duration as the plain second count used in every message.</summary>
    /// <param name="value">The duration.</param>
    /// <returns>The seconds, trimmed of trailing zeros.</returns>
    public static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
