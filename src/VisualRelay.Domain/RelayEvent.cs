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
    IReadOnlyDictionary<string, string>? Data = null);

