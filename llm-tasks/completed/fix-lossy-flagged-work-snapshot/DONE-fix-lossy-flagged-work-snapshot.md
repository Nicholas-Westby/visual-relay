# Fix the Lossy Flagged-Work Snapshot and Restore

Resuming a flagged task corrupts the working tree. Observed on 2026-07-06: clicking **Resume** on a
flagged task restored its flagged-work bundle and thereby (a) **deleted the tracked
`.relay/config.json` and `.relay/.gitignore`** — the app then showed the "Initialize this project"
screen because config.json was gone — and (b) **stripped the executable bit** from
`.githooks/command-guard`, `.githooks/commit-msg`, `.githooks/pre-commit`, `test.sh`, `me.sh`,
`tools/dotnet-test-files.sh`, and `visual-relay`. The de-executabled command-guard then made every
stage-8 attempt die in ~6 seconds with `swival exit 1: Error: command_middleware executable not
found or not executable: …/.githooks/command-guard`, burning all retries and escalations
(run 3/3, attempts 1–10) in about a minute before re-flagging. Make the snapshot/restore
round-trip faithful: tracked `.relay/` files and file modes must survive.

## Current state (researched)

- `src/VisualRelay.Core/Execution/FlaggedWorkStore.cs` `CaptureAsync` builds the kill-time
  snapshot in a **fresh temporary index** (`GIT_INDEX_FILE` pointed at a new path):
  1. `git add -A` stages everything;
  2. `git rm --cached -r -q --ignore-unmatch -- .relay/` then de-indexes ALL `.relay/` paths;
  3. `write-tree` / `commit-tree -p <runBaseSha>` produce the snapshot commit.
- **Defect 1 (deletions):** step 2's intent is to keep runtime relay metadata out of the bundle,
  but `.relay/config.json` and `.relay/.gitignore` are **tracked at the run base**, so removing
  them from the index makes the snapshot tree record them as *deleted* relative to base.
  `RestoreAsync` applies the snapshot with `git cherry-pick -n <snapshotSha>`, which faithfully
  deletes them from the working tree on resume.
- **Defect 2 (modes):** populating a **brand-new empty index** with `git add -A` under
  `core.fileMode=false` records every path as `100644` — there is no prior index entry to carry
  the mode, and git does not read modes from disk in that configuration. Verified: the run base
  has `100755` for `.githooks/command-guard`, `test.sh`, and `visual-relay`; the captured
  snapshot tree has `100644` for all of them. The cherry-pick then chmods the working files.
  (This repo had `core.fileMode=false` from a VM-shared-folder era; it has since been unset, but
  capture must be robust on any clone where it is false.)
- Restore call site: `RelayDriver.FlaggedWork.cs` ("On resume, restores the flagged working tree
  from a flagged-work bundle"). Why the exec bit matters: `SwivalSubagentRunner.BuildArguments`
  (`ProcessRunners.cs`) passes `--command-middleware <root>/.githooks/command-guard` whenever the
  file exists, and swival refuses to start when it is not executable.
- Existing test surfaces: `tests/VisualRelay.Tests/RelayDriverResumeTests.FlaggedWork.cs` and
  `.FlaggedWork2.cs` (capture/restore behavior through the driver), plus the sidecar assertions
  mentioned in `FlaggedWorkStore.cs`. There is no test covering tracked-`.relay` preservation or
  mode fidelity.

## What to build (TDD-first)

1. **Tests first** (extend `RelayDriverResumeTests.FlaggedWork*.cs` or add a focused
   `FlaggedWorkStoreTests.cs` using the same repo fixtures): a capture→restore round-trip on a
   test repo that has (a) a tracked `.relay/config.json` committed at base, (b) a tracked
   executable script (`100755` at base), and (c) `core.fileMode=false` set in the repo config.
   After capture on a dirty tree and restore onto a fresh checkout of base:
   - `.relay/config.json` still exists with its base content (never recorded as deleted);
   - the script's index/tree mode is still `100755` and the working file is executable;
   - the task's real edits are present (the existing behavior keeps working).
2. **Fix capture's `.relay/` handling** in `CaptureAsync`: instead of `git rm --cached -r --
   .relay/`, reset the `.relay/` paths in the temp index back to the run base's versions (e.g.
   `git restore --source=<runBaseSha> --staged -- .relay/` under the same `GIT_INDEX_FILE`
   environment, or an equivalent `ls-tree`-driven re-add of exactly the tracked-at-base `.relay/`
   entries). Result: the snapshot tree's `.relay/` content equals the base's, so the cherry-pick
   diff contains no `.relay/` changes at all — runtime metadata still never enters the bundle,
   and nothing gets deleted on restore.
3. **Fix capture's mode fidelity**: seed the temp index from the run base **before** staging
   (`git read-tree <runBaseSha>` with the `GIT_INDEX_FILE` environment, then `git add -A`) so
   tracked files carry their base modes through `add` even under `core.fileMode=false`, and/or
   run the capture-side `add` with `-c core.fileMode=true` so real on-disk bits are honored where
   the filesystem supports them. Either way the round-trip test in (1) must pass with
   `core.fileMode=false` set.
4. **Belt-and-braces on restore** (small, optional if (2)+(3) prove sufficient): after a
   successful cherry-pick in `RestoreAsync`, verify `.relay/config.json` still exists when it
   existed before the restore, and surface a loud failure (`RestoreResult`-level, not silent)
   instead of proceeding into a run with a deleted config.

## Done when

- The round-trip test proves: tracked `.relay/` files survive restore byte-for-byte, executable
  modes survive capture+restore under `core.fileMode=false`, and the task's actual edits are
  restored as before.
- Existing resume/flagged-work tests still pass; `./visual-relay check` passes (file-size guard,
  format verification, build, full test suite, README screenshot render).

## Guardrails

- Conventional Commits only (the `commit-msg` hook enforces the full ruleset). See
  `docs/commit-messages.md` and `AGENTS.md`.
- `FlaggedWorkStore.cs` is 253 lines — stay under the 300-line guard (extract a partial like the
  existing `FlaggedWorkStore` partial split if needed).
- All git operations must remain harness-side plumbing through `IGitInvoker` — no shelling out
  around it, no hook bypasses (matches the file's existing doc comment).
- Do not change the bundle format/ref layout (`refs/relay-snapshot/<taskId>`, sidecar JSON) —
  existing bundles on disk should still restore; only their `.relay`/mode defects go away for
  newly captured ones.
- Minimal diffs: change only what this task needs; do not reformat or reflow unrelated code.
