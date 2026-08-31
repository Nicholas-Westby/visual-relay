using VisualRelay.Core.Costs;

namespace VisualRelay.Tests;

/// <summary>
/// Both vision models are routed unpinned (no <c>:provider</c> suffix), so
/// Hugging Face picks the serving provider per request and the rate is not fixed.
/// Confirmed against the live catalog at router.huggingface.co/v1/models on
/// 2026-08-31: Qwen3-VL-235B is $0.30/$1.50 per 1M on novita and $0.20/$0.88 on
/// deepinfra; Qwen3-VL-30B is $0.20/$0.70 on novita and $0.15/$0.60 on deepinfra.
/// An unpinned route must therefore be priced at the dearest live provider, the
/// same call the catalog already makes for sticker over promotional rates: the
/// alternative silently under-bills whenever HF routes to the pricier host.
/// </summary>
public sealed class RelayPricingVisionRouteTests
{
    [Fact]
    public void Qwen3Vl235b_PricesAtTheDearestLiveProvider()
    {
        var vision = RelayPricing.Default["hf-qwen3-vl-235b"];

        Assert.Equal(0.30, vision.Input);
        Assert.Equal(1.50, vision.Output);
    }

    [Fact]
    public void Qwen3Vl30b_PricesAtTheDearestLiveProvider()
    {
        var fallback = RelayPricing.Default["hf-qwen3-vl-30b"];

        Assert.Equal(0.20, fallback.Input);
        Assert.Equal(0.70, fallback.Output);
    }

    /// <summary>
    /// The vision fallback must stay cheaper than the primary it backs off to,
    /// or a failover would raise the bill rather than lower it.
    /// </summary>
    [Fact]
    public void VisionFallback_IsCheaperThanTheVisionPrimary()
    {
        var primary = RelayPricing.Default["hf-qwen3-vl-235b"];
        var fallback = RelayPricing.Default["hf-qwen3-vl-30b"];

        Assert.True(fallback.Input < primary.Input);
        Assert.True(fallback.Output < primary.Output);
    }
}
