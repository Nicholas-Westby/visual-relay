# Bound the per-stage artifacts committed to git (and reconcile the gitignore policy)

Follow-up to the `activity-missing-for-completed-stages` fix (commit `15841c4`), which began
committing per-stage `.input.json` and `.report.json` artifacts to git (via `git add -f`, past
the blanket `*` in `.relay/.gitignore`) so completed-stage activity survives a fresh clone /
the host↔VM shared-repo split.

## Problem

This silently expands what the project commits and risks repo bloat, and it contradicts the
project's own stated policy:

- For **every executed stage**, up to two JSON files are now committed (~22 files for an 11-stage
  task). **Re-run/iterated** tasks commit a *different* attempt's pair, adding churn.
- `RelayGitignoreWriter` (the code that writes `.relay/.gitignore`) documents the canonical
  committed set as **exactly** `ledger.md`, `status.json`, `manifest.txt`, `*.seals`, and
  explicitly describes attempt reports as **"short-lived working-tree forensics … ~40 MB per
  ten-task drain."** Committing per-stage artifacts contradicts that comment, which is now stale
  and misleading for the next maintainer.

## Fix (preferred direction)

Keep cross-machine survival but **bound and reconcile** it:

1. Commit only the **final-attempt** `.input.json`/`.report.json` per stage (not every attempt),
   so resumed/retried tasks don't accumulate multiple attempts' artifacts in history. (Mirror
   whatever "highest/last attempt" selection the UI already uses to pick which artifact to show.)
2. **Update `RelayGitignoreWriter`'s canonical-record comment** (and any related docs, e.g.
   `docs/relay-artifacts.md`) so the documented committed set matches what is actually committed.
3. Confirm the commit-gate machinery stays correct: these files remain **out of the manifest**
   and excluded by `IsInternalArtifact`/`FindUncommittedAuthoredFilesAsync`, so manifest hashing
   and the "uncommitted authored files" check are unaffected (they already are — just don't
   regress this).

If, on closer inspection, cross-machine survival is NOT actually needed, the alternative is to
revert to a **UI-only read** of the on-disk `.relay/<taskId>/` artifacts (which already persist
on the same machine and are never deleted) and stop committing them at all. Pick one approach and
make code + policy comment agree; do not leave the current contradiction.

## How to verify
- Run a task whose stage is **retried** (multiple attempts): only ONE `.input.json`/`.report.json`
  pair per stage is committed (the final attempt), not one per attempt.
- The `.relay/.gitignore` policy comment (and `docs/relay-artifacts.md`) accurately lists the
  committed set.
- Completed-stage activity still displays (the original feature is preserved); commit-gate /
  manifest tests stay green.

## Constraints
- VR is general-purpose; no project-specific assumptions. `./visual-relay check` green; files
  ≤300 lines; Conventional Commit subjects.
