using System.Text.Json;

namespace VisualRelay.Core.Costs;

public sealed record RelayCostEstimate(
    string Model,
    double CostUsd,
    bool Priced,
    int PromptTokens,
    int CachedTokens,
    int OutputTokens,
    double DurationSeconds);

public static class RelayCostEstimator
{
    private const int OutputTokensPerTurn = 50;

    public static RelayCostEstimate EstimateReport(string reportPath)
    {
        using var stream = File.OpenRead(reportPath);
        using var document = JsonDocument.Parse(stream);
        return EstimateReport(document.RootElement);
    }

    public static RelayCostEstimate EstimateReport(JsonElement report)
    {
        var model = ReadString(report, "model");
        var llmCalls = report.TryGetProperty("timeline", out var timeline) && timeline.ValueKind == JsonValueKind.Array
            ? timeline.EnumerateArray().Where(IsLlmCall).ToArray()
            : [];
        var promptTokens = llmCalls.Sum(call => ReadInt(call, "prompt_tokens_est"));
        var answer = report.TryGetProperty("result", out var result) ? ReadString(result, "answer") : string.Empty;
        var outputTokens = (int)Math.Ceiling(answer.Length / 4.0) + llmCalls.Length * OutputTokensPerTurn;
        var stats = report.TryGetProperty("stats", out var statsValue) ? statsValue : default;
        var cachedTokens = ReadCachedTokens(stats);
        var uncachedTokens = Math.Max(0, promptTokens - cachedTokens);
        var duration = ReadDouble(stats, "total_llm_time_s") + ReadDouble(stats, "total_tool_time_s");

        if (!RelayPricing.Default.TryGetValue(model, out var pricing))
        {
            return new RelayCostEstimate(model, 0, false, promptTokens, cachedTokens, outputTokens, duration);
        }

        var cachedRate = pricing.CachedInput ?? pricing.Input;
        var usd = (uncachedTokens * pricing.Input + cachedTokens * cachedRate + outputTokens * pricing.Output) / 1_000_000d;
        return new RelayCostEstimate(model, usd, true, promptTokens, cachedTokens, outputTokens, duration);
    }

    private static bool IsLlmCall(JsonElement item) =>
        item.TryGetProperty("type", out var type) && type.GetString() == "llm_call";

    private static int ReadCachedTokens(JsonElement stats)
    {
        if (stats.ValueKind != JsonValueKind.Object ||
            !stats.TryGetProperty("prompt_cache", out var cache) ||
            cache.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return ReadInt(cache, "cached_tokens");
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static double ReadDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetDouble(out var parsed)
            ? parsed
            : 0;
}
