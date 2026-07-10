# Make the verify-worktree ignored-content overlay recursive so dep dirs stay writable

Two real-world verify failures against JS repos (axios: node_modules 187 MB, npm layout; zod:
node_modules 985 MB, pnpm layout) show the ≥64 MB branch of the ignored-content overlay is broken
for any dependency dir a test runner writes into. Both repos' stage-10/11 verifies failed with
`EPERM open <worktree>/node_modules/.vite-temp/vitest.config.*.timestamp-*.mjs` — vitest bundles a
temp config into `node_modules/.vite-temp/` at startup (standard vitest behavior, not repo-specific).
The implementation under verify was healthy; the snapshot itself cannot pass ANY vitest suite.

## Diagnosed root cause (verified, do not re-derive)

- `CreateVerifyWorktreeAsync` overlays each top-level git-ignored entry of the source repo into the
  verify worktree via `OverlayIgnoredEntry` (`src/VisualRelay.Core/Execution/RelayDriver.VerifyWorktree.cs`):
  entries at/above `IgnoredOverlayCopyMaxBytes` (64 MB) become ONE whole-directory symlink back to
  the source repo's real dir; smaller entries are copied by `CopyDirectoryResilient`
  (`RelayDriver.VerifyWorktreeCopy.cs`).
- A test-time write anywhere inside a whole-dir-symlinked entry resolves through the link to the
  REAL repo path, which the always-on sandbox correctly refuses (writes are confined to the verify
  cwd) → EPERM → verify red regardless of the change under test.
- The file-copy branch's own doc comment already names this exact hazard as the reason small
  entries are copied ("a test that WRITES a git-ignored path stays inside the sandboxed cwd instead
  of following a symlink OUT to the source (which nono --allow-cwd refuses → EPERM)") — the ≥64 MB
  branch reintroduces it wholesale.
- The recent symlink-preservation change (`preserve-symlinks-in-verify-worktree-overlay`) fixed the
  <64 MB copy branch dropping link entries; this task extends the same design to make the size
  decision PER ENTRY, RECURSIVELY, instead of once for the whole top-level dir.

## What to build

Replace the single top-level copy-vs-symlink decision in `OverlayIgnoredEntry` with one recursive
overlay walk that applies, per entry:

1. **Symlink entry in the source** (file or dir) → recreate the link node exactly as the
   symlink-preservation change does (never follow/traverse; relative targets verbatim; absolute
   targets inside the overlaid source subtree rewritten to the destination; others verbatim).
2. **Directory at/above the threshold** → single whole-dir symlink (read-mostly payload sharing,
   e.g. pnpm's `.pnpm` store, a huge `vendor/` tree) — today's ≥64 MB behavior, now applied at any
   depth so it captures the *bulk* without freezing the whole top-level dir.
3. **Directory below the threshold** → create a REAL directory at the destination and recurse.
4. **File** → copy (a file at/above the threshold may be symlinked, as today).

Effect on the failing class: `node_modules` itself becomes a real, writable directory whose small
children (`.bin` links, `.vite-temp`, `.cache`, lockfile metadata) are real/copied and writable in
the worktree, while huge payload subtrees (package trees, `.pnpm`) remain shared via links. A tool
that mkdirs/writes a new path under the dep root now succeeds inside the sandbox.

- Directory sizing must keep using the early-exiting `NonoRollbackSkipDirs.DirectoryMeetsSizeThreshold`
  (never fully sizes a huge tree). The walk must remain resilient per entry (errors swallowed, never
  abort worktree creation) and must never follow a link during traversal.
- Recursion must be bounded: cap depth (e.g. 16) and total real-copied bytes per top-level entry
  (reuse the existing threshold as the budget unit) so a pathological tree of millions of tiny files
  cannot make overlay unbounded — on hitting a bound, fall back to symlinking the remaining subtree
  and emit the existing `verify_overlay` advisory warn event with the entry and reason.
- Cleanup (`CleanupVerifyWorktreeAsync`/`UnlinkOverlaySymlinks`) must handle link nodes at ANY depth
  it may now encounter: never recursive-delete through a reparse point; targets outside the worktree
  must survive teardown untouched. Extend the unlink pass (or prove the existing delete order safe)
  and lock it in with a test.
- `BuildOutputOverlaySkipNames` semantics are unchanged (still consulted for top-level entries only).
- General-purpose only: no npm/pnpm/vitest-specific names or logic anywhere in the implementation.

## Tests (extend the verify-worktree family; red first against current whole-dir-symlink behavior)

Use the existing seam `CreateVerifyWorktreeForTestAsync(..., thresholdBytes)` with a tiny injected
threshold so no test writes large data:

- **Writable dep root (the axios/zod case):** source has ignored `deps/` whose subdir `big/`
  exceeds the injected threshold and sibling `small/` + file `meta.txt` do not. After overlay:
  `deps` is a REAL dir, `deps/big` is a symlink, `deps/small/…` and `deps/meta.txt` are real copies;
  creating a new file `deps/newcache/x` in the worktree succeeds and does NOT appear in the source
  repo's `deps/`.
- **Nested link preservation:** `deps/small/.bin/tool -> ../pkg/tool` (relative) survives as a link
  resolving inside the worktree copy.
- **Bounded recursion fallback:** with a depth/budget bound forced low, a deep tree still overlays
  (subtree symlinked) and the advisory event is emitted.
- **Teardown safety at depth:** a nested dir symlink several levels down pointing at an outside
  sentinel dir — sentinel intact after cleanup; worktree fully removed.
- **Regression:** the existing top-level ≥threshold whole-dir-symlink expectation is REPLACED by the
  new recursive expectation — update the prior task's tests only where their assertions encoded the
  old all-or-nothing behavior, keeping their safety intent (cycle, escape, dangling) fully covered.

## Verification

- `./test.sh` fully green including the new tests.
- The writable-dep-root test demonstrably fails against the pre-change whole-dir-symlink behavior.
