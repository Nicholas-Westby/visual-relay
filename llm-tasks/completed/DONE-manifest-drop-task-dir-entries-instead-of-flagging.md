# Drop manifest entries under the tasks dir instead of flagging the whole task

When the stage-4 (Plan) manifest contains a path under the configured tasks
directory (`config.TasksDir`, e.g. `llm-tasks/`), `RelayDriver` **hard-flags the
entire task**:

```csharp
// RelayDriver.cs (stage 4 handling, ~line 106)
var bad = manifest.FirstOrDefault(e => IsPathUnderDirectory(rootPath, e, config.TasksDir));
if (bad is not null)
    return await FlagAsync(rootPath, runId, taskId, taskDirectory, 4,
        $"manifest may not include task files under \"{config.TasksDir}\" (found \"{bad}\")",
        null, statusEntries, cancellationToken);
```

This is too strict. Some task types legitimately touch a file inside their own
task directory as **bookkeeping** — e.g. JobFinder's `remote-phrase-review`
appends a "completion table" to its own `llm-tasks/remote-phrase-review/
remote-phrase-review.md`. The Plan agent then lists that file in the manifest, and
the task is killed at stage 4 even though all of its actual code work
(new detection patterns + tests, suite green) is correct. The work is lost on the
flag-reset, so the task can never complete.

A manifest entry under the tasks dir is never a *code* change that the red gate
should strip or that verify covers, so the right behavior is to **exclude it from
the manifest and continue**, not to fail the task.

## Goal

At stage 4, when the manifest contains entries under `config.TasksDir`, **drop
those entries** (keep the rest), log/record that they were dropped, and proceed
with the cleaned manifest. Do not flag the task for this reason. Everything else
about manifest handling (writing it, the red gate, verify) stays the same, now
operating on the cleaned manifest.

## Approach (suggested)

- In the `stage.Number == 4` block of `RelayDriver.cs`, replace the
  `FirstOrDefault(... IsPathUnderDirectory ...)` → `FlagAsync` guard with a filter:
  partition the manifest into in-tree (under `config.TasksDir`) vs the rest; keep
  the rest as the manifest; if any were dropped, append a note to the status/ledger
  (e.g. "dropped N task-dir entries from manifest: …") so it's visible, then
  continue to `WriteManifestAsync`.
- Keep `IsPathUnderDirectory` as-is; only change the response from flag → filter.
- Optional: tighten the stage-4 (Plan) system prompt so the agent is told the
  manifest must list only code files, never files under the tasks dir — but the
  driver-side filter is the authoritative safety net.

## Files

- `src/VisualRelay.Core/Execution/RelayDriver.cs` (stage-4 manifest guard)
- `src/VisualRelay.Core/Execution/RelayStages.cs` only if the Plan system prompt is tightened

## Tests

Use the existing driver test doubles.

- A Plan stage whose manifest includes both a code file and a task-dir file
  (`llm-tasks/<task>/<task>.md`) → the task is NOT flagged at stage 4; the written
  manifest contains only the code file; the run proceeds to stage 5.
- A manifest of only task-dir entries → they are all dropped (empty/■ manifest is
  handled the same as a legitimately empty manifest), task not flagged for this
  reason.
- A manifest with no task-dir entries → unchanged behavior (regression guard).
- The dropped-entries note is recorded in the status/ledger.

## Notes

This unblocks `remote-phrase-review` (and any task that does task-dir bookkeeping)
without weakening the manifest's role for the red gate — task-dir files are never
valid red-gate strip targets, so excluding them is strictly correct. Keep
`RelayDriver.cs` within its line guard.
