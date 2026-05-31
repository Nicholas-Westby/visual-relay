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
    public string DisplayLine =>
        StageNumber is null ? EventName : $"s{StageNumber}/{Tier ?? "?"} {EventName}";

    public string DetailLine =>
        Data is { Count: > 0 }
            ? string.Join("  ", Data.Select(pair => $"{pair.Key}: {pair.Value}"))
            : Level;
}
