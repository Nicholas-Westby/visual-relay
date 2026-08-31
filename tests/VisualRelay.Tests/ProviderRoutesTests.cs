using VisualRelay.Core.Configuration;
using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the routing that replaces the generated proxy config: every model the
/// catalog can name must be reachable, every route must carry a real endpoint
/// and key, and the key-gated tier semantics must survive the move.
/// </summary>
public sealed class ProviderRoutesTests
{
    /// <summary>
    /// Every model in the catalog's chains has a route. A model that can be
    /// resolved but not sent to would fail only at request time, on a live run.
    /// </summary>
    [Fact]
    public void EveryChainedModel_HasARoute()
    {
        var missing = BackendConfigGenerator.Chains.Values
            .SelectMany(chain => chain)
            .Select(candidate => candidate.Model)
            .Where(model => model != "fallback")
            .Distinct(StringComparer.Ordinal)
            .Where(model => ProviderRoutes.For(model) is null)
            .ToList();

        Assert.True(missing.Count == 0,
            "these catalog models have no provider route: " + string.Join(", ", missing));
    }

    /// <summary>Every selectable model has a route too, for the same reason.</summary>
    [Fact]
    public void EverySelectableModel_HasARoute()
    {
        var missing = BackendConfigGenerator.SelectableModelsByTier.Values
            .SelectMany(models => models)
            .Where(model => model != "fallback")
            .Distinct(StringComparer.Ordinal)
            .Where(model => ProviderRoutes.For(model) is null)
            .ToList();

        Assert.True(missing.Count == 0,
            "these selectable models have no provider route: " + string.Join(", ", missing));
    }

    /// <summary>Every route is complete: endpoint, upstream id, key and a window.</summary>
    [Fact]
    public void EveryRoute_IsComplete()
    {
        foreach (var alias in ProviderRoutes.Aliases)
        {
            var route = ProviderRoutes.For(alias)!;

            Assert.True(route.Endpoint.IsAbsoluteUri, $"{alias} has no absolute endpoint");
            Assert.Equal("https", route.Endpoint.Scheme);
            Assert.False(string.IsNullOrWhiteSpace(route.UpstreamModel), $"{alias} has no upstream id");
            Assert.False(string.IsNullOrWhiteSpace(route.ApiKeyEnvVar), $"{alias} names no key");
            Assert.True(route.ContextWindow > 0, $"{alias} has no context window");
        }
    }

    /// <summary>
    /// Every route's key is one of the four the project still probes. A route
    /// naming a key nothing collects would silently never resolve.
    /// </summary>
    [Fact]
    public void EveryRouteKey_IsOneTheProjectProbes()
    {
        var probed = BackendConfigGenerator.ProviderKeyNames.ToHashSet(StringComparer.Ordinal);

        foreach (var alias in ProviderRoutes.Aliases)
            Assert.Contains(ProviderRoutes.For(alias)!.ApiKeyEnvVar, probed);
    }

    /// <summary>
    /// The Hugging Face routes carry the provider's own casing. Hugging Face is
    /// case-sensitive on request and lower-cases the id in its response, so this
    /// value is authoritative and a response id must never be echoed back.
    /// </summary>
    [Fact]
    public void HuggingFaceRoutes_KeepTheProviderCasing()
    {
        Assert.Equal(
            "Qwen/Qwen3-Coder-480B-A35B-Instruct:novita",
            ProviderRoutes.For("hf-qwen3-coder-next")!.UpstreamModel);
        Assert.Equal(
            "zai-org/GLM-5.3-Flash:zai-org",
            ProviderRoutes.For("hf-glm-5.3-flash")!.UpstreamModel);
    }

    /// <summary>
    /// The unpinned vision routes record the SMALLER of the two hosts' context
    /// windows, for the same reason pricing records the dearer rate: Hugging
    /// Face picks the host per request, and assuming the roomier one would
    /// over-fill the context whenever the other served.
    /// </summary>
    [Fact]
    public void UnpinnedVisionRoutes_RecordTheSmallerWindow()
    {
        Assert.Equal(131_072, ProviderRoutes.For("hf-qwen3-vl-235b")!.ContextWindow);
        Assert.Equal(131_072, ProviderRoutes.For("hf-qwen3-vl-30b")!.ContextWindow);
    }

    /// <summary>Headers carry the key as a bearer token and ask for a stream.</summary>
    [Fact]
    public void Headers_CarryTheKeyAndAskForAStream()
    {
        var headers = ProviderRoutes.For("deepseek-v4-pro")!.Headers("sk-example");

        Assert.Equal("Bearer sk-example", headers["Authorization"]);
        Assert.Equal("text/event-stream", headers["Accept"]);
    }

    /// <summary>Capabilities resolve through the route's provider.</summary>
    [Fact]
    public void Capabilities_ResolveThroughTheRoutesProvider()
    {
        Assert.True(ProviderRoutes.CapabilitiesFor("deepseek-v4-pro").CanDisableReasoning);
        Assert.False(ProviderRoutes.CapabilitiesFor("glm-5.3-flash").CanDisableReasoning);
        Assert.False(ProviderRoutes.CapabilitiesFor("kimi-k2").SupportsRequiredToolChoiceWhileThinking);
    }

    /// <summary>An unknown alias routes nowhere and gets conservative capabilities.</summary>
    [Fact]
    public void AnUnknownAlias_RoutesNowhere()
    {
        Assert.Null(ProviderRoutes.For("not-a-model"));
        Assert.False(ProviderRoutes.CapabilitiesFor("not-a-model").CanDisableReasoning);
    }

    /// <summary>
    /// A reasoning route gets a longer total budget than a fast one, but the
    /// inter-chunk idle budget stays tight on both: that is the stall detector,
    /// and reasoning streams as it is produced.
    /// </summary>
    [Fact]
    public void ReasoningRoutes_GetALongerTotalButATightIdleBudget()
    {
        var reasoning = ProviderRoutes.For("glm-5.3-flash")!.Timeouts;
        var fast = ProviderRoutes.For("deepseek-v4-flash")!.Timeouts;

        Assert.True(reasoning.Total > fast.Total);
        Assert.True(reasoning.InterChunkIdle <= TimeSpan.FromSeconds(60));
        Assert.True(fast.InterChunkIdle <= TimeSpan.FromSeconds(60));
    }

    // ── Tier chains ───────────────────────────────────────────────────────

    /// <summary>
    /// With only a Hugging Face token every text tier still resolves, ending at
    /// the floor model, so a user with one key can still run.
    /// </summary>
    [Fact]
    public void WithOnlyHuggingFace_TextTiersStillResolve()
    {
        var chains = BackendConfigGenerator.ResolveChains(new HashSet<string> { "HF_TOKEN" });

        foreach (var tier in (string[])["cheap", "balanced", "frontier"])
        {
            Assert.True(chains.ContainsKey(tier), $"{tier} did not resolve");
            Assert.NotEmpty(chains[tier]);
            foreach (var model in chains[tier])
                Assert.NotNull(ProviderRoutes.For(model));
        }
    }

    /// <summary>
    /// Vision is omitted entirely when no key backs it, rather than degrading to
    /// a text model. An image sent to a text model is answered confidently and
    /// wrongly, so a hard "model not found" is the safer failure.
    /// </summary>
    [Fact]
    public void Vision_IsOmittedRatherThanDegraded()
    {
        var withHf = BackendConfigGenerator.ResolveChains(new HashSet<string> { "HF_TOKEN" });
        var without = BackendConfigGenerator.ResolveChains(new HashSet<string> { "DEEPSEEK_API_KEY" });

        Assert.True(withHf.ContainsKey("vision"));
        Assert.False(without.ContainsKey("vision"));
    }

    /// <summary>
    /// A chain never names a tier alias: every hop is a concrete model the
    /// caller can send to directly, now that there is no proxy to resolve one.
    /// </summary>
    [Fact]
    public void EveryChainHop_IsAConcreteModel()
    {
        var keys = new HashSet<string>
        {
            "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY", "ZAI_API_KEY",
        };

        foreach (var (tier, chain) in BackendConfigGenerator.ResolveChains(keys))
            foreach (var model in chain)
                Assert.True(ProviderRoutes.For(model) is not null,
                    $"tier '{tier}' resolved to '{model}', which is not a routable model");
    }

    /// <summary>
    /// Chains are provider-diversified where the keys allow it, so one provider
    /// having an outage does not take the tier down with it.
    /// </summary>
    [Fact]
    public void ChainsAreProviderDiversified_WhenKeysAllow()
    {
        var keys = new HashSet<string>
        {
            "HF_TOKEN", "DEEPSEEK_API_KEY", "MOONSHOT_API_KEY", "ZAI_API_KEY",
        };

        var providers = BackendConfigGenerator.ResolveChains(keys)["frontier"]
            .Select(model => ProviderRoutes.For(model)!.ProviderName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(providers.Count > 1,
            "the frontier chain should span more than one provider: " + string.Join(", ", providers));
    }
}
