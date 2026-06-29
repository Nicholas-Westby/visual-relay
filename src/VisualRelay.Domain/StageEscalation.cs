namespace VisualRelay.Domain;

/// <summary>
/// General-purpose escalation ladder shared by the two escalation consumers so the
/// tier+turn math has a single source of truth: the in-process subagent retry loop
/// (<c>SwivalSubagentRunner.RunAsync</c>, which escalates on contract/exit/stall
/// failures it can see) and the driver's fix-verify loop (which escalates on an
/// external verify/test-red failure). Given a 1-based <em>run</em> index it yields:
/// <list type="bullet">
///   <item>the model <b>tier</b> — stepped up cheap→balanced→frontier once per
///   escalation, <b>capped at frontier</b> (run 1 = the stage's default tier); and</item>
///   <item>a turn/ceiling <b>multiplier</b> — the budget <b>doubles</b> each run
///   (1×, 2×, 4× → e.g. 200/400/800), <b>unless</b> the task is in flat 10×-boost
///   mode, where the per-escalation doubling is suppressed and turns stay flat at
///   the (already-10×) base while the tier still escalates.</item>
/// </list>
/// Pure and config-agnostic: the <em>cap</em> on the number of runs lives with the
/// caller (it consumes <c>RelayConfig.MaxStageFailures</c>), and the boosted base is
/// pre-computed by the caller, so this type keys only on generic tier/turn/run —
/// never on VR-repo or test-framework symbols.
/// </summary>
public static class StageEscalation
{
    /// <summary>
    /// One tier step up the ladder, capped at frontier: cheap→balanced→frontier,
    /// and frontier (or any unknown tier) stays frontier.
    /// </summary>
    public static string NextTier(string tier) => tier switch
    {
        "cheap" => "balanced",
        "balanced" => "frontier",
        _ => "frontier"
    };

    /// <summary>
    /// The tier for the 1-based <paramref name="run"/>: <see cref="NextTier"/>
    /// applied (run − 1) times to <paramref name="defaultTier"/> (run 1 = the
    /// stage's default tier), capped at frontier.
    /// </summary>
    public static string TierForRun(string defaultTier, int run)
    {
        var tier = defaultTier;
        for (var step = 1; step < run; step++)
            tier = NextTier(tier);
        return tier;
    }

    /// <summary>
    /// The turn/ceiling multiplier for the 1-based <paramref name="run"/> relative
    /// to the (effective run-1) base: <c>2^(run-1)</c> → 1, 2, 4 … in the normal
    /// case; flat <c>1</c> for every run when <paramref name="flatBoost"/> is set
    /// (the 10× boost holds turns flat — the doubling is suppressed).
    /// </summary>
    public static int RunMultiplier(int run, bool flatBoost) =>
        flatBoost ? 1 : 1 << Math.Max(0, run - 1);

    /// <summary>
    /// <paramref name="value"/> × <paramref name="multiplier"/> computed in
    /// <see cref="long"/> and saturated to <see cref="int.MaxValue"/>, so a large
    /// turn budget or wall-clock ceiling cannot overflow <see cref="int"/> to a
    /// negative value (a negative ceiling reads as "disabled" downstream).
    /// </summary>
    public static int Scale(int value, int multiplier)
    {
        var scaled = (long)value * multiplier;
        return scaled > int.MaxValue ? int.MaxValue : (int)scaled;
    }

    /// <summary>
    /// The turn budget for the 1-based <paramref name="run"/>:
    /// <paramref name="baseTurns"/> (the effective run-1 budget, already 10×-boosted
    /// by the caller when applicable) scaled by <see cref="RunMultiplier"/>.
    /// </summary>
    public static int TurnsForRun(int baseTurns, int run, bool flatBoost) =>
        Scale(baseTurns, RunMultiplier(run, flatBoost));
}
