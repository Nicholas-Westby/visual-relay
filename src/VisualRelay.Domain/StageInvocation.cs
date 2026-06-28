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
    string? TaskContext = null,
    string? TestCommand = null,
    string? PinnedSwivalProfileContent = null,
    int AbsoluteCeilingMs = 0,
    // Absolute path to the persisted FULL verify output (stageN-attemptM.verify-output.txt)
    // whose TAIL is in LastTestOutput. Surfaced in the prompt's ## Verify output section so
    // the agent can read the complete log when the tail isn't enough. Null when there is no
    // such artifact (e.g. non-verify stages).
    string? VerifyOutputPath = null);
