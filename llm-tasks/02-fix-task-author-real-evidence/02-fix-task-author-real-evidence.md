# Feed the fix-task author real failure evidence

## Problem

The "Create task to fix" button authored a confidently wrong task. On 2026-07-11 (task `show-cost-per-llm-model`, flagged with `guardCheck=red testCheck=green`), the FixTaskAuthor subagent's prompt contained the line "Here are the failures:" followed by **nothing** — no flag reason, no verify output, no ledger. The agent, whose filesystem access is restricted to `.swival/`, then scavenged `.swival/trash/` and stale `.swival/bg/` debug logs and fabricated a task around claims that were false for the tree (it asserted a view-model method was never called when it was, and that README tests failed when the test check was green). The real failure (a guard-command crash) never reached the prompt.

Three independent defects compound here.

### Defect 1 — the wrong directory is passed

`CreateFixTaskAsync` in `src/VisualRelay.App/ViewModels/MainWindowViewModel.FixTask.cs` does:

```csharp
var taskDirectory = SelectedTask.Task.TaskDirectory;
```

and passes that to `FixTaskAuthorRunner.RunAsync`. But `RelayTaskItem.TaskDirectory` is the **spec folder** (`llm-tasks/<slug>/` — it holds only the task markdown), while `FailedRunContextReader.Read` (in `src/VisualRelay.Core/Tasks/FailedRunContext.cs`) is documented and written to read the **run-artifact folder** `.relay/<taskId>/` — that is where `NEEDS-REVIEW`, `status.json`, `stage{N}-attempt{M}.verify-output.txt`, and `ledger.md` live. The reader is deliberately forgiving ("never throws — missing files produce null/empty fields"), so it silently returned an all-empty context.

### Defect 2 — the summarizer only understands test output

`FailedRunContextReader.ExtractSummary` keeps only lines containing `[FAIL]` and a final `Failed:`/`Passed:` tally. That is the shape of test-runner output. When the red check is the guard, bootstrap, or new-guard probe (e.g. a formatter crashing with an assembly-load stack trace), none of those markers exist and the summary digests to nothing — even with the right directory.

### Defect 3 — nothing refuses an empty prompt

`FixTaskAuthorRunner.RunAsync` (in `src/VisualRelay.Core/Execution/FixTaskAuthorRunner.cs`) builds the prompt via `BuildPrompt`, where every evidence section is conditional (`if (ctx.FlagReason is not null) …`). With an empty context it happily invokes the subagent with a prompt that announces failures and lists none, guaranteeing fabrication.

## Fix

### 1. Compute the artifact directory inside the runner

Remove the `taskDirectory` parameter from `FixTaskAuthorRunner.RunAsync` and compute the path internally from the parameters it already has:

```csharp
var taskDirectory = Path.Combine(rootPath, ".relay", taskId);
```

Update the only production caller (`CreateFixTaskAsync` in `MainWindowViewModel.FixTask.cs`) to drop the argument, and delete its `var taskDirectory = SelectedTask.Task.TaskDirectory;` line. This removes the wrong-directory failure mode structurally instead of fixing the one call site. Test callers (`tests/VisualRelay.Tests/MainWindowViewModelFixTaskTests.cs`, `.Execution.cs`, `.Capabilities.cs`, `FixTaskFakeRunners.cs`) adapt to the new signature; any fixture that previously planted artifacts in the spec folder must plant them under `<root>/.relay/<taskId>/` instead.

### 2. Include non-test check evidence in the reader

`RelayDriver` persists a structured per-check breakdown next to each verify output: `stage{N}-attempt{M}.verify-checks.json` (written by `TryPersistVerifyChecksJson` in `src/VisualRelay.Core/Execution/RelayDriver.VerifyObservability.cs`, a camelCase-serialized `SetupCheckResults` with keys `bootstrapCheck`/`bootstrapOutput`, `guardCheck`/`guardOutput`, `newGuardProbeCheck`/`newGuardProbeOutput`, `testCheck`/`testCommand`/`testExitCode`; each `*Check` is `"green"`, `"red"`, or null).

In `FailedRunContextReader.Read`, for each discovered `stage{N}-attempt{M}.verify-output.txt`, also try the sibling `.verify-checks.json`:

- For every non-test check whose value is `"red"`, prepend a labeled section to that `FailedVerifyOutput.Summary` — e.g. `guard check: red`, followed by the last 40 lines of the corresponding `*Output` string, capped at 4,000 characters.
- Keep the existing `[FAIL]`-line extraction for test output unchanged.
- When the combined summary would still be empty (no checks json, no `[FAIL]` markers, no tally line), fall back to the raw tail (last 40 lines, 4,000-character cap) of the verify-output file so the prompt is never evidence-free while an artifact exists.

The inclusion must stay content-agnostic: copy output verbatim; never pattern-match tool-specific text (Visual Relay processes arbitrary repos and stacks).

### 3. Refuse to author from an empty context

In `FixTaskAuthorRunner.RunAsync`, after `FailedRunContextReader.Read`, if `FlagReason` is null AND `VerifyOutputs` is empty AND `LedgerSummary` is null, return

```csharp
new FixTaskAuthorOutcome(false, null, null, null,
    $"No failure evidence found under .relay/{taskId} — cannot author a fix task.")
```

**without** invoking the subagent. The existing error path in `CreateFixTaskAsync` (`StatusText = $"Couldn't create fix task: {outcome.Error}"`) surfaces it; no UI changes needed.

## Rejected approaches — do not do these

- Do NOT keep the `taskDirectory` parameter and merely fix the caller's argument; the redundant parameter is how the seam drifted.
- Do NOT widen the fix-task subagent's filesystem access so it can "find evidence itself" — the restriction to `.swival/` stays; evidence belongs in the prompt.
- Do NOT add test-framework- or language-specific parsing (no xunit/dotnet/pytest patterns) to the reader.

## Tests

New file `tests/VisualRelay.Tests/FailedRunContextReaderTests.cs` (temp-dir fixtures shaped like `.relay/<taskId>/`):

- guard red: checks json with `guardCheck: "red"` and a multi-line `guardOutput` → summary contains `guard check: red` and the output tail verbatim.
- test red: verify-output containing `[FAIL]` lines and a tally → existing extraction preserved.
- no markers anywhere: summary falls back to the raw verify-output tail (never empty when the file has content).
- tail bounds: output longer than 40 lines / 4,000 chars is truncated to the cap.

In the existing fix-task test files:

- empty `.relay/<taskId>/` → `RunAsync` returns failure containing "No failure evidence", and the fake runner records zero invocations.
- artifacts planted under `<root>/.relay/<taskId>/` (a `NEEDS-REVIEW` file and one verify-output) → the fake runner's captured `TaskInput` prompt contains the flag reason and the verify evidence.

## Constraints

- `dotnet build VisualRelay.slnx` must succeed; all existing tests must pass (adapting signatures/fixtures is expected; never delete coverage).
- `BuildPrompt`'s section layout and the system prompt stay unchanged apart from receiving richer context.
- If a fact-count ratchet test guards a touched test class, bump the ratchet to match — never remove tests to satisfy it.
