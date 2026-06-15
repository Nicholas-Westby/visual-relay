# Fix three review-found harness bugs: manifest + prefix, dir-only existence, planning-phase WriteNeedsReviewMarker

Three bugs were found in a code-review pass over recently-committed harness tasks. Each is
contained and independent. Two are in the manifest-path pipeline; one is a missing
exception guard in the queue controller's planning phase.

Bugs 3 (backend.sh `local gen_rc=$?`) and 4 (plan-completeness retry cost file) from the
review were re-verified against the current tree and are NOT present: `local gen_rc=$?`
correctly captures `$?` via parameter expansion before `local` executes, and
`TryPlanCompletenessRetryAsync` already uses `ri.ReportFile` (the retry invocation's own
file, computed by `RelayAttempt.Next` after the original attempt's directory is created).
Only the three confirmed bugs below need fixes.

## Current state (researched)

### Bug 1 — `+`-prefix survives into the in-memory manifest

**`src/VisualRelay.Core/Execution/RelayDriver.cs:120–131`** (stage-4 path):

```csharp
manifest.Clear();
var raw = ReadStringArray(json, "manifest").Distinct(...).ToList();
...
foreach (var e in raw)
{
    if (IsPathUnderDirectory(rootPath, e, config.TasksDir))
        dropped.Add(e);
    else
        clean.Add(e);         // ← '+src/Foo.cs' lands here, prefix intact
}
manifest.AddRange(clean);     // ← in-memory manifest contains '+src/Foo.cs'
targetedTestCommand = BuildTargetedTestCommand(config, manifest);  // ← sees '+src/Foo.cs'
...
await WriteManifestAsync(taskDirectory, manifest, ...);  // strips '+' for the FILE, not the list
```

`WriteManifestAsync` (`src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs:16`) strips
`+` when writing `manifest.txt`, but `manifest` in memory still holds `+src/Foo.cs`.
Everything downstream of `manifest.AddRange(clean)` — `BuildTargetedTestCommand`,
`WorkingTreeHash`, and the stage-4 prompts for later stages that receive the manifest list —
sees the literal `+`-prefixed string, producing garbled test commands and wrong tree hashes
on any task that creates a new file.

The same flaw exists in the plan-completeness retry path:
**`src/VisualRelay.Core/Execution/RelayDriver.Snapshot.cs:107–110`**:

```csharp
manifest.AddRange(ReadStringArray(rj, "manifest")  // '+src/Foo.cs' not stripped
    .Distinct(...)
    .Where(...));
var ttc = BuildTargetedTestCommand(config, manifest);  // sees '+src/Foo.cs'
await WriteManifestAsync(...);                          // strips for file only
```

**Fix:** strip `+` at the point the `clean` list (and the retry manifest) is built, before
`manifest.AddRange`. In `RelayDriver.cs` replace the `clean.Add(e)` line:

```csharp
clean.Add(e.StartsWith('+') ? e[1..] : e);
```

In `RelayDriver.Snapshot.cs` replace the `ReadStringArray` chain:

```csharp
manifest.AddRange(ReadStringArray(rj, "manifest")
    .Distinct(StringComparer.Ordinal)
    .Where(e => !IsPathUnderDirectory(rootPath, e, config.TasksDir))
    .Select(e => e.StartsWith('+') ? e[1..] : e));
```

### Bug 2 — manifest existence check rejects directory entries

**`src/VisualRelay.Core/Execution/ProcessRunners.ManifestValidation.cs:46–48`**:

```csharp
var missing = existingEntries
    .Where(p => !File.Exists(Path.Combine(targetRoot, p)))  // ← Directory.Exists not checked
    .ToList();
```

A manifest entry that is a real directory (e.g. a project root or a path to a folder that
the agent intends to enumerate) passes the `!p.StartsWith("+")` filter (it is not a new
file) and then fails `File.Exists` — causing a false-positive rejection with "does not exist
in the target repo." The gitignore check (`ResolveManifestFilesToStageAsync`) already
handles directories without special-casing, so the existence check must match.

**Fix:** accept either file or directory:

```csharp
var missing = existingEntries
    .Where(p => !File.Exists(Path.Combine(targetRoot, p))
             && !Directory.Exists(Path.Combine(targetRoot, p)))
    .ToList();
```

### Bug 3 — Phase-1 `WriteNeedsReviewMarker` call is unguarded

**`src/VisualRelay.Core/Queue/RelayQueueController.cs:160–168`** (planning phase):

```csharp
if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
{
    WriteNeedsReviewMarker(taskId, outcome.Reason ?? "Needs review");  // ← no try/catch
    var idx = IndexOf(taskId);
    ...
}
```

Phase-2 (execute phase, same file line 251) already wraps its `WriteNeedsReviewMarker`
call:

```csharp
try { WriteNeedsReviewMarker(outcome.TaskId, outcome.Reason ?? "Needs review"); }
catch { DrainSummaryLog.Write(..., "WriteNeedsReviewMarker failed"); }
```

A secondary `IOException` from the phase-1 call (e.g. a filesystem permission error on
the task directory marker file) would abort the queue drain entirely during the planning
phase — even though the plan outcome itself was recorded and the circuit-breaker logic
immediately below continues to reference `outcome`. The phase-2 guard was added in commit
`3182307` but the corresponding phase-1 site was missed.

**Fix:** apply the same guard at `RelayQueueController.cs:162`:

```csharp
if (outcome.Status == RelayTaskOutcomeStatus.Flagged)
{
    try { WriteNeedsReviewMarker(taskId, outcome.Reason ?? "Needs review"); }
    catch { DrainSummaryLog.Write(RootPath, drainRunId, taskId, "plan", "exception", "WriteNeedsReviewMarker failed"); }
    var idx = IndexOf(taskId);
    ...
}
```

## What to build

Write the failing test(s) first (TDD). The three bugs are independent; they may land in
separate commits.

### 1. Strip `+` prefix into the in-memory manifest (two sites)

**Test first** — add to `tests/VisualRelay.Tests/` (new file or extend existing manifest
driver tests, e.g. `RelayDriverGitCommitTests`):

- `Stage4_NewFilePrefix_IsStrippedFromInMemoryManifest` — via `ScriptedSubagentRunner`,
  stage-4 returns `{ "manifest": ["+src/New.cs", "src/Existing.cs"], "plan": "..." }`;
  assert that the `targetedTestCommand` passed to subsequent invocations does NOT contain
  `+src/New.cs` and that `manifest.txt` written to disk also has the bare path.

- `PlanCompletenessRetry_NewFilePrefix_IsStrippedFromInMemoryManifest` — same setup but
  the coverage gap triggers a plan-completeness retry; the retry's manifest also has
  `+src/New.cs`; assert the in-memory manifest after retry contains `src/New.cs`, not
  `+src/New.cs`.

**Fix** as described in Bug 1 above: two one-line changes, one in `RelayDriver.cs:129`
and one in `RelayDriver.Snapshot.cs:107`.

### 2. Accept directories in the manifest existence check

**Test first** — add to the existing `SwivalSubagentRunnerManifestExistenceTests` (or
equivalent):

- `ManifestExistenceCheck_DirectoryEntry_IsAccepted` — create a temp directory; include
  its relative path (without `+`) in the manifest; assert `CheckManifestAgainstGitignoreAsync`
  returns null (no error), not the "does not exist" rejection string.

- `ManifestExistenceCheck_MissingNeitherFileNorDir_IsRejected` — a path that is neither a
  file nor a directory still produces the rejection message (regression guard).

**Fix** as described in Bug 2 above: one-line change at
`ProcessRunners.ManifestValidation.cs:47`.

### 3. Guard Phase-1 `WriteNeedsReviewMarker`

**Test first** — add to `RelayQueueControllerTests` (or create
`RelayQueueControllerDrainTests.cs`):

- `DrainAsync_PlanningPhase_WriteNeedsReviewMarkerIOException_ContinuesDrain` —
  use a subclass or fake that throws `IOException` from `WriteNeedsReviewMarker`; confirm
  the drain does NOT throw and the task is still recorded as flagged in the results.

**Fix** as described in Bug 3 above: wrap the call at `RelayQueueController.cs:162` in the
same `try { ... } catch { DrainSummaryLog.Write(...) }` pattern used at line 251.

## Done when

- **Bug 1 fixed and tested:** `manifest` in memory never contains a `+`-prefixed path after
  stage 4 (main path or plan-completeness retry). `BuildTargetedTestCommand` and
  `WorkingTreeHash` receive clean paths. The two new driver tests assert this; one confirms
  the retry path. `manifest.txt` on disk is unchanged (still written without `+`).

- **Bug 2 fixed and tested:** `CheckManifestAgainstGitignoreAsync` returns null when a
  manifest entry names an existing directory. `ManifestExistenceCheck_DirectoryEntry_IsAccepted`
  passes; the regression guard for genuinely missing paths still passes.

- **Bug 3 fixed and tested:** an `IOException` from `WriteNeedsReviewMarker` during planning
  phase does not abort the drain; the drain continues and the outcome is logged.
  `DrainAsync_PlanningPhase_WriteNeedsReviewMarkerIOException_ContinuesDrain` passes.

- `./visual-relay check` green after all changes.

- No modified file exceeds 300 lines.

- Conventional Commit subject candidates:
  - `fix(driver): strip + prefix from in-memory manifest at stage-4 acceptance`
  - `fix(manifest): accept directory entries in existence check`
  - `fix(drain): guard planning-phase WriteNeedsReviewMarker against IOException`
