using System.Text;
using System.Text.Json.Nodes;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Builds the message <see cref="ReplayTransport"/> throws on a miss.
/// <para>
/// Modelled on GitSim's <c>Unsupported()</c>, which names the full argv it will
/// not emulate rather than quietly succeeding. The equivalent here is worse than
/// an argv: the request is a few kilobytes of JSON and the key is a hash, so
/// "no cassette for 3f2a..." tells a reader nothing. The message therefore names
/// the exact file that was looked for and diffs the request against the nearest
/// recorded one, so the changed field is visible without opening anything.
/// </para>
/// </summary>
internal static class CassetteMiss
{
    private const int MaxReportedDifferences = 8;

    /// <summary>Describes a miss: what was wanted, where it would live, and what is closest.</summary>
    /// <param name="store">The store that was searched.</param>
    /// <param name="provider">The provider directory name.</param>
    /// <param name="scenario">The scenario directory name.</param>
    /// <param name="request">The request that missed.</param>
    /// <param name="operation">The transport method that missed, for the message.</param>
    /// <returns>The full, copy-pasteable miss message.</returns>
    public static string Describe(
        CassetteStore store,
        string provider,
        string scenario,
        ProviderRequest request,
        string operation)
    {
        var canonical = CassetteKey.Canonical(request);
        var key = CassetteKey.Hash(canonical.ToJsonString());
        var model = canonical["model"] is { } node ? node.ToJsonString() : "(none)";

        var message = new StringBuilder();
        message.AppendLine(
            "ReplayTransport: cassette miss. Replay NEVER falls through to the network.");
        message.AppendLine($"  operation: {operation}");
        message.AppendLine($"  request  : {canonical["method"]} {canonical["path"]}  model={model}");
        message.AppendLine($"  provider : {provider}   scenario: {scenario}");
        message.AppendLine($"  key      : {key}  (canonicalizer v{CassetteKey.Version})");
        message.AppendLine($"  expected : {store.PathFor(provider, scenario, key)}  (not on disk)");
        AppendNearest(message, store, provider, scenario, canonical);
        message.Append(
            "  Fix the request, or re-record this exchange with RecordingTransport.");
        return message.ToString();
    }

    private static void AppendNearest(
        StringBuilder message,
        CassetteStore store,
        string provider,
        string scenario,
        JsonObject canonical)
    {
        var candidates = store.ReadScenario(provider, scenario);
        if (candidates.Count == 0)
        {
            message.AppendLine(
                $"  nearest  : (no cassettes recorded under {provider}/{scenario} yet)");
            return;
        }

        var nearest = candidates
            .Select(record => (record.Key, Differences: CassetteDiff.Compare(record.Request, canonical)))
            .OrderBy(pair => pair.Differences.Count)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .First();

        var differences = nearest.Differences;
        message.AppendLine(
            $"  nearest  : {nearest.Key}.json  ({Count(differences.Count)} vs this request)");

        foreach (var difference in differences.Take(MaxReportedDifferences))
        {
            message.AppendLine($"      {difference.Path}");
            message.AppendLine($"        cassette: {difference.Cassette}");
            message.AppendLine($"        request : {difference.Request}");
        }

        if (differences.Count > MaxReportedDifferences)
            message.AppendLine($"      ... and {differences.Count - MaxReportedDifferences} more");
    }

    private static string Count(int differences) =>
        differences == 1 ? "1 differing field" : $"{differences} differing fields";
}
