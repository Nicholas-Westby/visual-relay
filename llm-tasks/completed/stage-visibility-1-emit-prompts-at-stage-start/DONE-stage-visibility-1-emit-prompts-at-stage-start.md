# Persist each stage's system + input prompt at stage start, and emit a live event

Visual Relay is going to surface every pipeline stage's prompts and output in the UI (full design: `docs/superpowers/specs/2026-06-20-per-stage-prompt-output-tabs-design.md`). The UI must show a stage's **system prompt** and its assembled **input prompt** the *moment the stage starts* — but today the only on-disk copy of the assembled prompt is `stage{N}-attempt{M}.report.json` (`.task`), which swival writes when the stage *finishes*, and the system prompt is never persisted at all. This task adds a tiny artifact written at invocation, plus a lightweight "ready" event. It is backend-only and lands green on its own; the UI that consumes it is tasks 2–4.

This design is decided — implement exactly this, no alternatives.

## Current state (researched)

- The assembled prompt is built by `SwivalSubagentRunner.BuildPrompt(invocation)` (`src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs:95-132`) and added as the final argv just before launch: `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs:101-104` (`arguments.Add(correctivePriorOutput is not null ? BuildCorrectivePrompt(...) : BuildPrompt(attemptInvocation))`), launched at `:111`. The per-attempt report path is computed at `:69-71` (`stage{N}-attempt{M}.report.json`); `attemptInvocation` (carrying `TraceDirectory`, `ReportFile`, `Stage`, `RunId`, `TargetRoot`, `TaskName`, `Tier`) is built at `:72`.
- `report.json` is written by swival at stage END; it is the only persisted copy of the assembled prompt (under `.task`) and does **not** contain the system prompt. Its `timestamp` field (ISO-8601 UTC) is the reliable time signal — file mtimes are unreliable in this repo (host/VM sync resets them).
- The **effective** system prompt for an attempt is `attemptInvocation.Stage.SystemPrompt` — already correct for the stage-6 front-load swap, which `RelayDriver` bakes into the stage before invocation (`src/VisualRelay.Core/Execution/RelayDriver.cs:88-89`: `effectiveStage = stage with { Tier = "cheap", SystemPrompt = RelayStages.ConfirmImplementationSystemPrompt }`).
- Events: `RelayEvent` is a record `(Timestamp, Level, EventName, RunId, RootPath, TaskId, StageNumber, Tier, Attempt, Data)` where `Data` is `IReadOnlyDictionary<string,string>` (`src/VisualRelay.Domain/RelayEvent.cs`). The runner already holds an `_eventSink` and publishes events — see the `trace` event construction at `ProcessRunners.Helpers.cs:289-299` and the `command_dropped` event at `ProcessRunners.Helpers.cs:39-53` for the exact `PublishAsync(new RelayEvent(...))` shape to mirror.
- Attempt parsing helper: `RelayAttempt.TryParse` / `RelayAttempt.AttemptNumber(name)` over the `stage{n}-attempt{k}` scheme (`src/VisualRelay.Core/Traces/RelayAttempt.cs:16-35`).

## What to build

TDD — write the failing unit tests first; keep the logic pure so it tests without a process launch.

1. **`StageInputArtifact`** (`src/VisualRelay.Core/Execution/StageInputArtifact.cs`): a record `(int Version, int Stage, int Attempt, string Name, string SystemPrompt, string InputPrompt, string Timestamp)` plus pure static helpers:
   - `string PathFor(string reportFilePath)` — derive the sibling `.input.json` path from a `stage{N}-attempt{M}.report.json` path (swap the extension).
   - `void Write(string reportFilePath, StageInputArtifact data)` — serialize with `System.Text.Json` to `PathFor(reportFilePath)`.
   - `bool TryRead(string inputJsonPath, out StageInputArtifact data)` — tolerant read (false on missing/corrupt; never throws).
   - `string? LatestPath(string taskDirectory, int stageNumber)` — enumerate `stage{stageNumber}-attempt*.input.json` and return the one with the highest `RelayAttempt.AttemptNumber` (NOT mtime), or null.
2. **Write the artifact at invocation.** In `ProcessRunners.RunAsync.cs`, immediately after the prompt is added to `arguments` (~`:104`, before `BuildLaunchTarget`), build a `StageInputArtifact` from `attemptInvocation` — `SystemPrompt = attemptInvocation.Stage.SystemPrompt`, `InputPrompt =` the exact prompt string just added to `arguments`, `Name = attemptInvocation.Stage.Name`, `Stage`/`Attempt` from the attempt, `Timestamp =` the start time as ISO-8601 UTC — and call `StageInputArtifact.Write(reportFile, …)`. Use the same `reportFile` local already computed at `:69-71`. Best-effort: a write failure must not abort the run.
3. **Emit a `stage_input` event** right after the write, via `_eventSink`, mirroring the `trace`/`command_dropped` construction: `EventName = "stage_input"`, correct `RunId/RootPath/TaskId/StageNumber/Tier/Attempt`, and `Data` carrying **only** lightweight metadata — `Data["systemBytes"]`, `Data["inputBytes"]` (UTF-8 byte lengths) and `Data["path"]` (the `.input.json` path). Do **not** put the prompt text in the event: it can be ~70 KB and the file is the source of truth; the event is only a "written — reload from disk" nudge.

## Done when

- New unit tests pass and fail against today's (absent) code first, covering: `PathFor` derivation; `Write`→`TryRead` round-trip; `TryRead` returns false (no throw) on a missing/corrupt file; `LatestPath` picks the max attempt number even when an earlier attempt's file has a newer mtime; the `stage_input` event carries byte counts + path but not the full prompt.
- A real (or harness-simulated) stage run writes `stage{N}-attempt{M}.input.json` next to the report and publishes one `stage_input` event at invocation. `systemPrompt` equals `RelayStages.All[N-1].SystemPrompt` for a normal stage and the Confirm-implementation prompt for a front-loaded stage 6.
- `./visual-relay check` is green; changed files < 300 lines; Conventional Commit, e.g. `feat(core): persist stage system+input prompt at stage start`.
- **Coordinate (the implementer sees only this file):** tasks `stage-visibility-2/3/4` consume this. Task 2 reads `stage{N}-attempt{M}.input.json` for the system + input prompt and listens for the `stage_input` event to refresh the selected stage live; it reads the stage **output** from `report.json` (`.task` is the input; `result.answer` / the ledger is the output). Do not build any UI here.
