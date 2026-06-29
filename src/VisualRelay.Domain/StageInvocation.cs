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
    string? VerifyOutputPath = null,
    // True when this stage's turn budget is in flat 10×-boost mode (the task is in
    // RelayConfig.BoostTurnsTaskIds): RunAsync's per-escalation turn DOUBLING is then
    // suppressed (turns stay flat at the already-10× MaxTurns) while the tier still
    // escalates. False (default) = the normal doubling ladder applies.
    bool IsTurnBoosted = false,
    // Per-invocation cap on RunAsync's own internal tier+turn escalation. Default
    // int.MaxValue = escalation is governed solely by RelayConfig.MaxStageFailures.
    // The driver's fix-verify loop passes 0 because IT owns the (external, verify-red)
    // escalation across its iterations — so the inner RunAsync must not also escalate
    // (which would double-count the 3-run budget for that stage).
    int MaxSelfEscalations = int.MaxValue);
