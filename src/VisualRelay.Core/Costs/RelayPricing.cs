namespace VisualRelay.Core.Costs;

// Rates are USD per 1,000,000 tokens, matching Relay's pricing.json unit.
internal sealed record ModelPricing(double Input, double Output, double? CachedInput = null);

internal static class RelayPricing
{
    public static IReadOnlyDictionary<string, ModelPricing> Default { get; } =
        new Dictionary<string, ModelPricing>(StringComparer.Ordinal)
        {
            ["cheap-kimi"] = new(0.14, 0.28, 0.0028),
            ["balanced-kimi"] = new(0.435, 0.87, 0.003625),
            ["frontier"] = new(0.95, 4.0, 0.16),
            ["vision"] = new(0.30, 1.50),
            ["claude-opus-1m"] = new(5.0, 25.0),
            ["claude-sonnet"] = new(3.0, 15.0),
            ["claude-haiku"] = new(1.0, 5.0),
            ["gpt-5"] = new(1.25, 10.0),
            ["hf-qwen3-coder-next"] = new(0.30, 1.30),
            ["kimi-k2"] = new(0.95, 4.0, 0.16)
        };
}
