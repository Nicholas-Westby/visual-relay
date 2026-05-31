namespace VisualRelay.Domain;

public sealed record StageInvocation(
    RelayStageDefinition Stage,
    string Tier,
    string RunId,
    string TargetRoot,
    string TaskName,
    string TaskInput,
    string LedgerSoFar,
    IReadOnlyList<string> Manifest,
    IReadOnlyList<string> LogSources,
    string TraceDirectory,
    string ReportFile,
    int MaxTurns,
    string? LastTestOutput = null,
    string? TaskContext = null);
