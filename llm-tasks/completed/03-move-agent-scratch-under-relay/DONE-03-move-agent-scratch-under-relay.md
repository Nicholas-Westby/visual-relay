# Move agent scratch from .relay-scratch/ to .relay/scratch/ and retire the legacy dir

`.relay-scratch/` is half-retired and leaks debris into target repos.
Observed 2026-07-18 in a workspace: a stale
`.relay-scratch/match_screenshot.png` left by a visual-review agent sits in
the working tree indefinitely and shows up as an untracked file in every git
client, because nothing ignores or cleans it there. Current state of the
code: the agent prompt still names it a protected scratch area
(ProcessRunners.Prompt.cs:28 — which is why agents write screenshots there),
the screenshot tool roots its scratch at `.relay-scratch/screenshot-root`
(tools/VisualRelay.Screenshots/Program.cs:23), six exclusion lists carry it
(GitCommitter.Untracked.cs:9, WorktreeFilter.cs:21, WorktreeResetter.cs:24,
RelayDriver.CodeChangeGate.cs:101, RelayDriver.VerifyWorktree.cs:192,
NonoRollbackSkipDirs.cs:35), and the only cleanup
(BackendLifecycle.Start.cs:234, already labeled "legacy") deletes it solely
under the VR repo's own root — never the workspace being driven. Meanwhile
`.relay/` self-ignores everything via the nested `.relay/.gitignore`
(RelayGitignoreWriter: `*` with negations for `.gitignore`/`config.json`), so
scratch relocated inside it is invisible to git in every target repo with no
gitignore changes at all.

## Prescribed approach

Make `.relay/scratch/` the one canonical agent scratch location, point the
prompt and the screenshot tool at it, and add a workspace-side cleanup for
the legacy directory. Keep the legacy name in the exclusion lists for now —
stale `.relay-scratch/` dirs exist in the wild, and dropping the exclusions
before cleanup has shipped would let leftover debris pollute untracked-file
accounting mid-transition.

### Steps

1. Prompt (ProcessRunners.Prompt.cs:28): drop `.relay-scratch/` from the
   protected-paths line and add explicit scratch guidance so agents stop
   inventing locations — protected list becomes `{TasksDir}/, .relay/,
   .swival/`, plus a clause directing throwaway artifacts (screenshots,
   probes) to `.relay/scratch/`.
2. Screenshot tool (tools/VisualRelay.Screenshots/Program.cs:23):
   `scratchRoot` becomes `.relay/scratch/screenshot-root`.
3. Workspace cleanup: at workspace open/refresh (the same path that
   discovers task folders), best-effort delete `<workspaceRoot>/.relay-scratch`
   when present, logging one line — mirror the BackendLifecycle legacy
   pattern (best-effort, deletion failure never blocks). The VR-repo-root
   cleanup in BackendLifecycle.Start stays as is.
4. Exclusion lists: unchanged this task (`.relay/` entries already cover the
   new location; `.relay-scratch` entries stay for the transition). Leave a
   short comment on one of them noting they become removable once the
   workspace cleanup has been out for a while.
5. Nothing to do for gitignore: `.relay/.gitignore`'s `*` already ignores
   `scratch/`; confirm no force-add path (GitCommitter proof files) could
   ever pick up scratch content — scratch must never be committed.

## Tests (red first)

- Prompt-builder test: the rendered protected-paths line contains
  `.relay/scratch` guidance and does NOT contain `.relay-scratch`.
- Screenshot tool: scratch root resolves under `.relay/scratch/` (arg/path
  unit test, no Avalonia needed).
- Workspace cleanup: temp workspace with a populated `.relay-scratch/` →
  refresh removes it; absent dir → no-op; locked/undeletable dir → cleanup
  reports but does not throw and the refresh completes.

## Verification

`./visual-relay check` green. Manual: open a workspace seeded with a stale
`.relay-scratch/file.png` — it disappears on open and `git status` is clean;
run a visual-review task and confirm new screenshots land under
`.relay/scratch/` and never appear as untracked files.
