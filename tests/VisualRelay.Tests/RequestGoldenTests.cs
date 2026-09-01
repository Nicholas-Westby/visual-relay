using System.Text.Json.Nodes;
using VisualRelay.Core.Llm;
using VisualRelay.Core.Llm.Routing;

namespace VisualRelay.Tests;

/// <summary>
/// Goldens the exact request body sent per model, per stage shape.
/// <para>
/// The layer this replaced ran with <c>drop_params: true</c>, silently stripping
/// parameters providers reject. A body could therefore look correct in config
/// and never reach the wire that way. Goldening the serialized bytes puts any
/// change to them in a diff, and the live suite in
/// <see cref="LiveRequestAcceptanceTests"/> is what proves a provider actually
/// takes the shape.
/// </para>
/// </summary>
public sealed class RequestGoldenTests
{
    /// <summary>
    /// The shapes worth goldening. The last two are the ones that differ per
    /// provider: reasoning effort is dropped unless the provider accepts that
    /// value, and <c>tool_choice</c> is omitted for a provider that rejects
    /// required choice while thinking.
    /// </summary>
    private static readonly (string Stage, bool WithTools, string? Effort, bool Require)[] Shapes =
    [
        ("ideate", false, null, false),
        ("plan", true, null, false),
        ("plan-required-tools", true, null, true),
        ("ideate-no-reasoning", false, "none", false),
    ];

    private static JsonNode ToolSchema() =>
        JsonNode.Parse("""
            {
              "type": "function",
              "function": {
                "name": "read_file",
                "description": "Reads a file.",
                "parameters": {
                  "type": "object",
                  "properties": { "path": { "type": "string" } },
                  "required": ["path"]
                }
              }
            }
            """)!;

    private static string Body(string alias, bool withTools, string? effort, bool require)
    {
        var route = ProviderRoutes.For(alias)!;
        return ChatRequestBuilder.Build(
            [
                new ChatMessage("system", "You are a stage of a relay pipeline."),
                new ChatMessage("user", "Do the thing and answer with the contract."),
            ],
            new ChatRequestOptions(
                route.UpstreamModel, Stream: true, ReasoningEffort: effort, RequireToolCall: require),
            ProviderCapabilityCatalog.For(route.ProviderName),
            withTools ? [ToolSchema()] : null);
    }

    /// <summary>Every routed model's serialized body matches its golden.</summary>
    /// <param name="alias">The catalog alias.</param>
    [Theory]
    [MemberData(nameof(RoutedModels))]
    public void EveryRoutedModel_SerializesToItsGolden(string alias)
    {
        foreach (var (stage, withTools, effort, require) in Shapes)
            RequestGolden.AssertOrUpdate(alias, stage, Body(alias, withTools, effort, require));
    }

    /// <summary>Every alias the catalog can route to.</summary>
    /// <returns>One row per alias.</returns>
    public static TheoryData<string> RoutedModels()
    {
        var data = new TheoryData<string>();
        foreach (var alias in ProviderRoutes.Aliases.OrderBy(a => a, StringComparer.Ordinal))
            data.Add(alias);
        return data;
    }

    /// <summary>
    /// Every routed model has a golden, so adding a route without goldening its
    /// body is caught here rather than at runtime.
    /// </summary>
    [Fact]
    public void EveryRoutedModel_HasAGolden()
    {
        var missing = new List<string>();
        foreach (var alias in ProviderRoutes.Aliases)
            foreach (var (stage, _, _, _) in Shapes)
                if (!File.Exists(RequestGolden.PathFor(alias, stage)))
                    missing.Add($"{alias}/{stage}");

        Assert.True(missing.Count == 0,
            $"routes with no goldened request body (run with {RequestGolden.UpdateEnvVar}=1, "
            + "and pair the result with a live run):\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// No golden exists for a model the catalog cannot route. A stale golden is
    /// worse than none: it asserts a shape nothing sends.
    /// </summary>
    [Fact]
    public void NoGolden_OutlivesItsRoute()
    {
        var aliases = ProviderRoutes.Aliases.ToHashSet(StringComparer.Ordinal);

        var orphans = RequestGolden.All()
            .Select(g => g.Model)
            .Distinct(StringComparer.Ordinal)
            .Where(model => !aliases.Contains(model))
            .ToList();

        Assert.True(orphans.Count == 0,
            "goldens for models nothing routes to:\n" + string.Join("\n", orphans));
    }
}
