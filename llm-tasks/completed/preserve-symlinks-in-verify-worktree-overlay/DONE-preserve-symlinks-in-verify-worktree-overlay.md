# Preserve symlinks when the verify worktree copies small ignored dirs

A real-world run against expressjs/express (Node.js, `testCmd: "npm test"`) produced a phantom
red verify: the implementation was complete and correct — inside the agent stages `npm test`
exited 0 with 1269 tests passing — but the driver's isolated verify failed with exit 127
`sh: line 1: mocha: command not found`, identically on stage 10 and all three stage-11 fix-verify
attempts, so a correct change was flagged `verify failed after 3 fix-verify attempts` and ~75
minutes of pipeline time were spent on a failure the agents could not possibly fix.

## Diagnosed root cause (verified against the live artifacts, do not re-derive)

- `RunIsolatedVerifyAsync` snapshots the repo into a detached-HEAD worktree and overlays each
  top-level git-ignored entry: entries ≥ 64 MB are symlinked whole, entries **below** the
  threshold are copied via `CopyDirectoryResilient`
  (`src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktree.cs:124-138`,
  `RelayDriver.VerifyWorktreeCopy.cs:16`).
- `CopyDirectoryResilient` **skips every reparse point** (`RelayDriver.VerifyWorktreeCopy.cs:47-48`)
  — a blanket "never touch symlinks" rule meant to prevent cycle traversal and escapes.
- express's `node_modules` measured 62 MB → copy branch. Every launcher in `node_modules/.bin/`
  is a *relative symlink* (`mocha -> ../mocha/bin/mocha`) in npm layouts, so the copied
  `node_modules` had **no `.bin` entries at all**. `npm test` prepends `<cwd>/node_modules/.bin`
  to PATH, found no `mocha`, and the suite could never run. The verify-output artifact shows npm
  itself started fine (`> express@5.2.1 test`) and then `sh: line 1: mocha: command not found`.
- This is a general class, not an npm quirk: pnpm layouts make the *entire* top level of
  `node_modules` symlinks into `.pnpm/`; Python venvs symlink their interpreter; any dependency
  dir under the size threshold with internal links is silently gutted. The ≥64 MB symlink branch
  and the <64 MB copy branch must present equivalent content or verify diverges from the agent's
  workspace — the worst kind of failure, because fix-verify burns attempts on a harness artifact.

## What to build

1. **Recreate symlink entries instead of dropping them** in `CopyDirectoryResilient`
   (`src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktreeCopy.cs`). Semantics of `cp -RP`:
   - Never *follow* or *traverse into* a link during the walk (this keeps today's cycle- and
     escape-safety: a directory symlink is recreated as a link node, never enumerated).
   - For each entry whose attributes include `ReparsePoint`, read its target
     (`FileSystemInfo.LinkTarget`) and recreate it at the destination:
     `Directory.CreateSymbolicLink` when the entry is a `DirectoryInfo`, `File.CreateSymbolicLink`
     otherwise.
   - **Relative targets are recreated verbatim** — they resolve within the copied tree naturally
     (this is the npm `.bin` case and the pnpm case).
   - **Absolute targets that point inside the source directory being copied** must be rewritten to
     the corresponding path under the destination directory (prefix swap `sourceDir` → `destDir`),
     so the snapshot stays self-contained. Absolute targets pointing anywhere else are recreated
     verbatim (read-mostly sharing, same trust level as the ≥64 MB whole-dir symlink branch).
   - Dangling targets are recreated like any other link (`cp -RP` does the same); creation errors
     stay per-entry-swallowed exactly like today's copy errors — the resilience contract
     ("a copy failure must NEVER abort worktree creation") is unchanged.
2. **Keep cleanup link-safe with nested links present.** `CleanupVerifyWorktreeAsync`
   (`RelayDriver.VerifyWorktree.cs:237-247`) currently unlinks only *top-level* symlinks before
   `git worktree remove` + recursive delete. Copied dirs can now contain *nested* dir-symlinks.
   Verify (and lock in with a test) that the teardown path removes a nested directory symlink as
   a link node and never deletes *through* it into content outside the worktree. If any step of
   the current teardown would traverse a nested dir-link, harden it (e.g. extend the unlink pass
   to walk copied subtrees or delete links before dirs) — but do not weaken the existing
   "never recursive-delete a reparse point" rule for top-level links.
3. **Surface total overlay failures instead of staying silent.** The per-entry overlay call is
   wrapped in a swallow-all catch (`RelayDriver.VerifyWorktree.cs:130-137`). Keep the
   never-abort contract, but when overlaying a top-level ignored entry throws, publish a `warn`
   event (same pattern as `EmitMutatedTreeAdvisoryAsync`, e.g. kind `verify_overlay_skipped`)
   naming the entry and the exception message, so a gutted snapshot is diagnosable from run.log.

## Constraints

- General-purpose only: no npm/pnpm/venv-specific logic anywhere — the fix is "copies preserve
  link nodes", not "special-case .bin".
- Do not change the 64 MB `IgnoredOverlayCopyMaxBytes` threshold, the build-output skip list, or
  which entries get overlaid.
- macOS and Linux are the supported platforms for this path; use the BCL symlink APIs, no shelling
  out to `cp`.

## Tests (red first — they must fail against the current skip-all-links behavior)

Extend the existing verify-worktree test family (the seam
`CreateVerifyWorktreeForTestAsync(sourcePath, worktreeId, runId, ct, thresholdBytes)` exists
precisely for this, including injecting a tiny threshold to force either branch — follow the
patterns already used by the tests that cover the copy/symlink boundary):

- **npm-shape regression (the express case):** source repo with git-ignored `deps/` containing
  `deps/pkg/bin/tool` (real file) and `deps/.bin/tool -> ../pkg/bin/tool` (relative symlink);
  overlay with a threshold forcing the COPY branch; assert the worktree's `deps/.bin/tool` exists,
  is a symlink, and resolves to the *worktree's* copy of `pkg/bin/tool` (not the source's).
- **Cycle safety:** `deps/a/loop -> ..` (or two dirs linking to each other); overlay must complete
  without hanging or throwing, and the link nodes exist at the destination.
- **Absolute-internal rewrite:** `deps/link -> /abs/path/to/source/deps/real` is rewritten to the
  destination's `deps/real`.
- **Escape + teardown safety:** `deps/out -> <sentinel dir outside the repo>` (directory symlink);
  after overlay AND after `CleanupVerifyWorktreeForTestAsync`, the sentinel directory and its
  contents are untouched.
- **Dangling link:** `deps/ghost -> ./missing` recreates without error and without aborting the
  rest of the copy (a sibling real file must still be copied).

## Verification

- `./test.sh` fully green (this repo's standard gate), including the new tests.
- The new tests demonstrably fail if the `ReparsePoint → continue` skip is restored (that is the
  red state this task starts from).
