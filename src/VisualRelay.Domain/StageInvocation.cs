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
    string? FullTestCommand = null,
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
    // Per-invocation cap on RunAsync's own internal tier+turn escalation. Default
    // int.MaxValue = escalation is governed solely by RelayConfig.MaxStageFailures.
    // The driver's fix-verify loop passes 0 because IT owns the (external, verify-red)
    // escalation across its iterations — so the inner RunAsync must not also escalate
    // (which would double-count the 3-run budget for that stage).
    // Repo-relative tasks directory (RelayConfig.TasksDir). When set, BuildPrompt
    // emits a "Protected paths" header line naming it as queue bookkeeping that is
    // never part of the task's diff. Null (default) omits the line.
    string? TasksDir = null,
    // True when TestCommand is the project's WHOLE test command because no narrower
    // one could be built (no {files} form configured, or no runnable test file in the
    // manifest). BuildPrompt then says so in ## Verify command, so a stage told to run
    // "ONLY the targeted test command" does not read the full suite as a contradiction.
    bool TestCommandIsFullSuite = false,
    // Repo-relative paths of the well-known contributor/agent instruction files (and
    // the .cursor/rules directory) BuildInvocation found under the stage's root.
    // Only Research (stage 2) ever gets a non-empty list; every other stage keeps the
    // default null. BuildPrompt renders a "Repository instructions" section naming
    // them right after the Manifest block, and omits it entirely when null or empty.
    IReadOnlyList<string>? RepositoryInstructionFiles = null,
    // True when the stage is answered from its prompt alone and must be sent with
    // no tool definitions at all. A single-turn stage offered a tool catalog spends
    // its one turn calling one, then reports an exhausted turn budget with no
    // answer; the stage-5 diff audit did exactly that on every run that fired it.
    bool WithoutTools = false);
