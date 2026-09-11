using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Tests;

/// <summary>
/// Per-model timeout ceilings, now expressed as the four budgets each route
/// carries rather than one number per model.
/// <para>
/// There used to be a single hard ceiling per model — 75s for DeepSeek, 120s for
/// the HF routes, 480s for the frontier ones — because one number was all the
/// layer underneath could express. The
/// in-process client separates connect, time-to-first-byte, inter-chunk idle and
/// total, so a stall is caught by the idle budget in seconds while a legitimately
/// slow reasoning model still gets the wall clock it needs. The guard the old
/// numbers provided is kept: every route carries explicit budgets, and a
/// reasoning model is given longer to start talking than a fast one.
/// </para>
/// </summary>
public sealed class ModelCatalogPerModelTimeoutTests
{
    private static readonly string[] Reasoning = ["glm-5.3-flash", "hf-glm-5.3-flash", "kimi-k2"];

    private static readonly string[] Fast =
    [
        "deepseek-flash", "deepseek-v4-pro", "deepseek-v4-flash",
        "deepseek-v4-flash-vision-exp",
        "hf-qwen3-coder-next", "hf-qwen3-vl-235b", "hf-qwen3-vl-30b",
    ];

    /// <summary>
    /// Every routed model carries explicit, non-zero budgets, so no request can
    /// hang indefinitely on a byte-0 upstream stall. The global request timeout
    /// alone proved insufficient for the 2026-06-10 wedge.
    /// </summary>
    [Fact]
    public void EveryRoute_CarriesExplicitBudgets()
    {
        foreach (var alias in ProviderRoutes.Aliases)
        {
            var budgets = ProviderRoutes.For(alias)!.Timeouts;

            Assert.True(budgets.Connect > TimeSpan.Zero, $"{alias} has no connect budget");
            Assert.True(budgets.TimeToFirstByte > TimeSpan.Zero, $"{alias} has no TTFB budget");
            Assert.True(budgets.InterChunkIdle > TimeSpan.Zero, $"{alias} has no idle budget");
            Assert.True(budgets.Total > TimeSpan.Zero, $"{alias} has no total budget");
        }
    }

    /// <summary>
    /// The routes cover every model, and each is classified as reasoning or
    /// fast. A new route added to neither list fails here rather than silently
    /// inheriting whichever profile it was given.
    /// </summary>
    [Fact]
    public void EveryRoute_IsClassifiedAsReasoningOrFast()
    {
        var classified = Reasoning.Concat(Fast).ToHashSet(StringComparer.Ordinal);

        var unclassified = ProviderRoutes.Aliases.Where(a => !classified.Contains(a)).ToList();
        Assert.True(unclassified.Count == 0,
            "routes classified as neither reasoning nor fast: " + string.Join(", ", unclassified));

        var stale = classified.Where(a => ProviderRoutes.For(a) is null).ToList();
        Assert.True(stale.Count == 0, "classified models that no longer route: " + string.Join(", ", stale));
    }

    /// <summary>
    /// A reasoning model gets longer to produce its first byte than a fast one.
    /// GLM and Kimi always reason before emitting content, so their first-token
    /// latency is by nature longer; holding them to a fast model's TTFB kills
    /// healthy calls.
    /// </summary>
    [Fact]
    public void AReasoningModel_GetsLongerToStartTalking()
    {
        var slowestFastTtfb = Fast.Max(a => ProviderRoutes.For(a)!.Timeouts.TimeToFirstByte);
        var quickestReasoningTtfb = Reasoning.Min(a => ProviderRoutes.For(a)!.Timeouts.TimeToFirstByte);

        Assert.True(quickestReasoningTtfb > slowestFastTtfb,
            $"reasoning TTFB {quickestReasoningTtfb} is not longer than fast TTFB {slowestFastTtfb}");
    }

    /// <summary>
    /// A reasoning model also gets a longer wall clock: 412s was the observed
    /// worst-case healthy Review, so its total must clear that with room, while
    /// staying far below the stage cap.
    /// </summary>
    [Fact]
    public void AReasoningModel_ClearsTheObservedWorstCaseReview()
    {
        foreach (var alias in Reasoning)
            Assert.True(
                ProviderRoutes.For(alias)!.Timeouts.Total > TimeSpan.FromSeconds(412),
                $"{alias}'s total budget does not clear the 412s observed worst-case Review");
    }

    /// <summary>
    /// The idle budget is what catches a stall now, so it must be short enough
    /// to fire long before the total does.
    /// </summary>
    [Fact]
    public void TheIdleBudget_FiresWellBeforeTheTotal()
    {
        foreach (var alias in ProviderRoutes.Aliases)
        {
            var budgets = ProviderRoutes.For(alias)!.Timeouts;
            Assert.True(budgets.InterChunkIdle * 4 < budgets.Total,
                $"{alias}'s idle budget {budgets.InterChunkIdle} is not comfortably "
                + $"below its total {budgets.Total}");
        }
    }
}
