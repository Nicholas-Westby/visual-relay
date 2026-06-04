namespace VisualRelay.Domain;

public sealed record RelayEvent(
    DateTimeOffset Timestamp,
    string Level,
    string EventName,
    string RunId,
    string RootPath,
    string? TaskId = null,
    int? StageNumber = null,
    string? Tier = null,
    int? Attempt = null,
    IReadOnlyDictionary<string, string>? Data = null)
{
    public bool IsAttention => Level is "warn" or "error";

    public string DisplayLine =>
        StageNumber is null ? EventName : $"s{StageNumber}/{Tier ?? "?"} {EventName}";

    public string DetailLine =>
        Data is { Count: > 0 }
            ? string.Join("  ", OrderedData().Select(pair => $"{pair.Key}: {pair.Value}"))
            : Level;

    private IEnumerable<KeyValuePair<string, string>> OrderedData()
    {
        var priority = new[] { "time", "cost", "sessionCost", "model", "name", "reason" };
        foreach (var key in priority)
        {
            if (Data?.TryGetValue(key, out var value) == true)
            {
                yield return new KeyValuePair<string, string>(key, value);
            }
        }

        foreach (var pair in Data?.Where(pair => !priority.Contains(pair.Key)) ?? [])
        {
            yield return pair;
        }
    }
}
