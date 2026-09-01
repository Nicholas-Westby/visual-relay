using System.Text.Json;
using VisualRelay.Core.Costs;
using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Tests;

/// <summary>
/// Checks the hand-recorded Hugging Face prices and context windows against the
/// router's own catalog, captured as a fixture.
/// <para>
/// Hugging Face routes are the awkward ones. An UNPINNED route has no stable
/// envelope at all: the router picks a serving host per request, and the two
/// hosts behind the 235B vision model differ by 50% on price and by a factor of
/// two on context. So the rule is to record the worst case — the dearest price
/// and the smallest window — because assuming the friendlier host under-bills
/// and over-fills whenever the other one serves.
/// </para>
/// <para>
/// The comparison runs against a fixture rather than the live endpoint so the
/// fast suite stays hermetic. Refreshing the fixture is then a deliberate act
/// that surfaces a price change as a failing test, instead of leaving the
/// recorded numbers to drift quietly out of date.
/// </para>
/// </summary>
public sealed class HuggingFaceCatalogParityTests
{
    /// <summary>An alias, the catalog id behind it, and the host it is pinned to.</summary>
    private sealed record Route(string Alias, string CatalogId, string? PinnedHost);

    private static readonly Route[] Routes =
    [
        new("hf-qwen3-coder-next", "Qwen/Qwen3-Coder-480B-A35B-Instruct", "novita"),
        new("hf-qwen3-vl-235b", "Qwen/Qwen3-VL-235B-A22B-Instruct", null),
        new("hf-qwen3-vl-30b", "Qwen/Qwen3-VL-30B-A3B-Instruct", null),
        new("hf-glm-5.3-flash", "zai-org/GLM-5.3-Flash", "zai-org"),
    ];

    private static JsonElement Catalog()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "HuggingFace", "router-models.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static IReadOnlyList<JsonElement> ProvidersFor(string catalogId)
    {
        foreach (var model in Catalog().GetProperty("data").EnumerateArray())
            if (model.GetProperty("id").GetString() == catalogId)
                return [.. model.GetProperty("providers").EnumerateArray()
                    .Where(p => p.TryGetProperty("status", out var s) && s.GetString() == "live")];

        return [];
    }

    private static (double Input, double Output)? DearestPrice(IReadOnlyList<JsonElement> providers)
    {
        var priced = providers
            .Where(p => p.TryGetProperty("pricing", out var pricing)
                && pricing.ValueKind == JsonValueKind.Object)
            .Select(p => p.GetProperty("pricing"))
            .Select(p => (Input: p.GetProperty("input").GetDouble(), Output: p.GetProperty("output").GetDouble()))
            .ToList();

        return priced.Count == 0 ? null : (priced.Max(p => p.Input), priced.Max(p => p.Output));
    }

    private static int? SmallestWindow(IReadOnlyList<JsonElement> providers)
    {
        var windows = providers
            .Where(p => p.TryGetProperty("context_length", out var c) && c.ValueKind == JsonValueKind.Number)
            .Select(p => p.GetProperty("context_length").GetInt32())
            .ToList();

        return windows.Count == 0 ? null : windows.Min();
    }

    /// <summary>The fixture holds every model this project routes to.</summary>
    [Fact]
    public void TheFixture_CoversEveryRoutedModel()
    {
        foreach (var route in Routes)
            Assert.True(ProvidersFor(route.CatalogId).Count > 0,
                $"the captured catalog has no live provider for {route.CatalogId}");
    }

    /// <summary>
    /// Every routed model is priced at the DEAREST live host. Recording the
    /// cheaper one under-bills every request the router sends to the other.
    /// </summary>
    [Fact]
    public void RecordedPrices_MatchTheDearestLiveHost()
    {
        var problems = new List<string>();

        foreach (var route in Routes)
        {
            if (DearestPrice(ProvidersFor(route.CatalogId)) is not { } dearest) continue;
            if (!RelayPricing.Default.TryGetValue(route.Alias, out var recorded))
            {
                problems.Add($"{route.Alias} has no pricing entry");
                continue;
            }

            if (Math.Abs(recorded.Input - dearest.Input) > 0.0001)
                problems.Add($"{route.Alias} input: recorded {recorded.Input}, dearest live {dearest.Input}");
            if (Math.Abs(recorded.Output - dearest.Output) > 0.0001)
                problems.Add($"{route.Alias} output: recorded {recorded.Output}, dearest live {dearest.Output}");
        }

        Assert.True(problems.Count == 0,
            "recorded Hugging Face pricing has drifted from the captured catalog:\n"
            + string.Join("\n", problems));
    }

    /// <summary>
    /// Every routed model's context window is no larger than the SMALLEST live
    /// host offers. Assuming the roomier host over-fills the context whenever
    /// the other one serves, and the model then truncates without saying so.
    /// </summary>
    [Fact]
    public void RecordedWindows_AreNoLargerThanTheSmallestLiveHost()
    {
        var problems = new List<string>();

        foreach (var route in Routes)
        {
            if (SmallestWindow(ProvidersFor(route.CatalogId)) is not { } smallest) continue;
            var recorded = ProviderRoutes.For(route.Alias)?.ContextWindow;
            if (recorded is null)
            {
                problems.Add($"{route.Alias} has no route");
                continue;
            }

            if (recorded > smallest)
                problems.Add(
                    $"{route.Alias} window: recorded {recorded}, smallest live host offers {smallest}");
        }

        Assert.True(problems.Count == 0,
            "recorded context windows exceed what the captured catalog guarantees:\n"
            + string.Join("\n", problems));
    }

    /// <summary>
    /// The unpinned vision routes really do have hosts that disagree, which is
    /// what makes the worst-case rule necessary rather than merely cautious. If
    /// this ever stops being true the rule can be revisited.
    /// </summary>
    /// <param name="catalogId">An unpinned vision model in the catalog.</param>
    [Theory]
    [InlineData("Qwen/Qwen3-VL-235B-A22B-Instruct")]
    [InlineData("Qwen/Qwen3-VL-30B-A3B-Instruct")]
    public void UnpinnedRoutes_HaveHostsThatDisagree(string catalogId)
    {
        var providers = ProvidersFor(catalogId);

        var prices = providers
            .Where(p => p.TryGetProperty("pricing", out var pricing)
                && pricing.ValueKind == JsonValueKind.Object)
            .Select(p => p.GetProperty("pricing").GetProperty("input").GetDouble())
            .Distinct()
            .ToList();

        var windows = providers
            .Where(p => p.TryGetProperty("context_length", out var c) && c.ValueKind == JsonValueKind.Number)
            .Select(p => p.GetProperty("context_length").GetInt32())
            .Distinct()
            .ToList();

        Assert.True(prices.Count > 1, $"{catalogId} no longer has hosts at different prices");
        Assert.True(windows.Count > 1, $"{catalogId} no longer has hosts with different windows");
    }

    /// <summary>
    /// A pinned route names a host the catalog actually lists, so the pin cannot
    /// silently point at a provider that has gone away.
    /// </summary>
    [Fact]
    public void PinnedRoutes_NameAHostTheCatalogLists()
    {
        foreach (var route in Routes.Where(r => r.PinnedHost is not null))
        {
            var hosts = ProvidersFor(route.CatalogId)
                .Select(p => p.GetProperty("provider").GetString())
                .ToList();

            Assert.Contains(route.PinnedHost, hosts);
            // The route's upstream id must carry the pin, or the router is free
            // to pick any host and the fixed envelope is a fiction.
            Assert.Contains(
                ":" + route.PinnedHost,
                ProviderRoutes.For(route.Alias)!.UpstreamModel,
                StringComparison.Ordinal);
        }
    }
}
