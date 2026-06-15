# Harness follow-up: harden the stage-5 worktree discard against data loss

A code review of `harness-authortests-test-only-scope` (commit `bec07bb`) found that the new
stage-5 discard primitive — `WorktreeFilter.DiscardNonTestEditsAsync`, which **deletes untracked
files and reverts tracked files to HEAD** — is under-hardened for a file-destroying operation.
Six defects were confirmed by reading the code and reproducing the git-level behavior. Two are
outright data-loss (the stage destroys the very test it exists to protect) and the rest let
production edits silently survive the discard or revert files that must be preserved.

All of these live in one file and share one root cause: agent-supplied paths are compared to
git-emitted paths with no normalization, and destructive git calls run unchecked. Fixing them
together keeps the primitive coherent.

## Current state (researched)

> **Freshness contract:** Locate every anchor by searching for the quoted snippet — never by line
> number. If a quoted snippet no longer exists, treat this researched state as stale and re-verify
> against the current `WorktreeFilter` before building.

The primitive is `src/VisualRelay.Core/Execution/WorktreeFilter.cs`, method
`DiscardNonTestEditsAsync`. It enumerates dirty tracked files (`git diff --name-only`,
`git diff --cached --name-only`, `git ls-files --deleted`) and untracked files
(`git ls-files --others --exclude-standard`), subtracts the agent's `testFiles`, reverts the rest,
and deletes untracked leftovers.

### Defect 1 — `+` new-file prefix is not stripped → the just-authored test is DELETED  (data loss, HIGH)

The comparison set is built verbatim:

```csharp
var testSet = new HashSet<string>(testFiles, StringComparer.Ordinal);
```

But the harness trains agents to mark **new** files with a leading `+` (the stage-4 manifest
convention), and every other consumer strips it — `WriteManifestAsync`, `EarlyImplementationDetector`,
`Snapshot`, and `RedGate.ComputeStripSet` all remove a leading `+` before matching. Here it is not
stripped. So when the agent declares a brand-new test as `"+tests/FooTests.cs"`, the path emitted by
`git ls-files --others` is `tests/FooTests.cs`, which is **not** in `testSet`, so it falls into
`nonTestUntracked` and is destroyed:

```csharp
var full = Path.Combine(rootPath, rel);
if (File.Exists(full))
    File.Delete(full);
```

The stage exists to preserve the agent's authored tests; with the `+` convention it deletes them.
(`RedGate.ComputeStripSet` has the milder sibling effect — a `+`-prefixed testFile is also not
subtracted from the strip set — but the deletion here is the data-loss one.)

### Defect 2 — staged rename aborts the whole revert batch → production edits survive  (HIGH)

All non-test tracked paths are reverted in a single batched pathspec:

```csharp
if (nonTestTracked.Count > 0)
{
    await GitAsync(rootPath, ["checkout", "HEAD", "--", .. nonTestTracked], cancellationToken);
}
```

If the agent renamed a file during stage 5 (`git mv`), the new path is **not in HEAD**, so
`git checkout HEAD -- <newpath>` errors with `pathspec ... did not match` and git aborts the
**entire** batch — every other production edit in `nonTestTracked` survives un-reverted, defeating
the discard, and the renamed-from file stays deleted. (Reproduced: a bad pathspec leaves a sibling
modification intact.)

### Defect 3 — every git exit code is swallowed → false "clean tree" / false ledger  (MEDIUM)

`GitAsync` returns `(int ExitCode, string Output, bool TimedOut)`, but **no** call site inspects it
(lines reading `git diff`, `git diff --cached`, `git ls-files --deleted`, `git ls-files --others`,
and the `git checkout HEAD --` revert). Two consequences:
- A failed/timed-out enumeration returns empty `Output` → the dirty set is empty → the method
  reports a clean discard while silently protecting nothing.
- A failed revert (defect 2, a lock, a permission error) is ignored, yet the returned
  `WorktreeFilterResult.TrackedDiscarded` still lists those paths as discarded — the ledger asserts
  a revert that did not happen.

### Defect 4 — tracked-revert path lacks the artifact / tasks-dir guards  (MEDIUM)

The untracked branch protects Visual Relay's own data and the task spec:

```csharp
var nonTestUntracked = dirtyUntracked
    .Where(p => !testSet.Contains(p)
        && !IsInternalArtifact(p)
        && !IsUnderTasksDir(rootPath, p, tasksDir))
    .Distinct(StringComparer.Ordinal)
    .ToList();
```

…but the tracked branch does not — it only subtracts `testSet`:

```csharp
var nonTestTracked = dirtyTracked
    .Where(p => !testSet.Contains(p))
    .Distinct(StringComparer.Ordinal)
    .ToList();
```

So a **tracked** file the agent edits under `.relay/`, `.relay-scratch/`, `.swival/`, or the
tasks-dir (e.g. the task spec itself) is sent straight to `git checkout HEAD --` and reverted, while
the identical untracked case is protected. The guards must apply symmetrically.

### Defect 5 — no path normalization → legit test reverted/deleted on separator or `./` variance  (MEDIUM)

Matching is exact `StringComparer.Ordinal` against git's canonical forward-slash output, but the
agent's JSON is uncontrolled. A `tests\FooTests.cs` (backslash), `./tests/FooTests.cs`, or
leading-`/` variant misses `testSet` and the legitimate test is reverted/deleted. The codebase's own
`IsTestFile` already does `Replace('\\','/')` precisely because agents emit backslashes; this filter
is strictly more brittle.

### Defect 6 — case-sensitive match on a case-insensitive host  (LOW/MEDIUM)

`StringComparer.Ordinal` is case-sensitive, but the default macOS volume is case-insensitive (and
`IsUnderTasksDir` in this same file already uses `OrdinalIgnoreCase`). A case-divergent testFiles
entry (`tests/footests.cs` vs on-disk `tests/FooTests.cs`) misses `testSet` and the agent's test is
clobbered.

## What to build

Keep the method's contract and happy-path behavior identical; harden the matching and the git calls.
**Stay toolchain-agnostic** — do not hard-code any project's path layout or file extensions.

1. **One normalization helper for agent-supplied repo-relative paths.** Introduce a single private
   helper (or a small reusable `RepoRelativePath` utility if you prefer to also route the other
   consumers through it later — but at minimum use it here) that, given a path, returns a canonical
   form: strip a single leading `+`, replace `\` with `/`, trim a leading `./` and any leading `/`,
   and trim a trailing `/`. Build `testSet` from **normalized** testFiles, and normalize every
   git-emitted tracked/untracked path before the `Contains` checks. Use a comparer that matches host
   filesystem semantics — `OrdinalIgnoreCase` on case-insensitive hosts (gate via
   `RuntimeInformation` or simply use `OrdinalIgnoreCase` to match the sibling `IsUnderTasksDir`).
   This fixes defects 1, 5, 6.

2. **Make the revert safe against staged renames/deletes and check the result.** Replace the single
   blind `git checkout HEAD -- <all>` with a revert that does not abort the whole batch when one path
   is absent from HEAD, and that restores both index and working tree (consider
   `git restore --source=HEAD --staged --worktree -- <paths>`, or per-path handling that unstages a
   rename's new path and deletes it as an untracked addition). Inspect `ExitCode`/`TimedOut` and, on
   failure, return a failure signal (e.g. a `Failed` flag / non-null error on `WorktreeFilterResult`)
   rather than reporting those paths as cleanly discarded. This fixes defect 2 and the revert half of
   defect 3.

3. **Fail closed on a failed enumeration.** If any of the four enumeration git calls returns non-zero
   or times out, do **not** proceed to delete/revert on the resulting (empty) set — surface the
   failure to the caller so `HandleStage5Async` can flag the stage instead of silently protecting
   nothing. This fixes the enumeration half of defect 3.

4. **Apply the artifact + tasks-dir guards to the tracked set too.** Add `!IsInternalArtifact(p)`
   and `!IsUnderTasksDir(rootPath, p, tasksDir)` to the `nonTestTracked` filter so tracked edits to
   `.relay/`, `.relay-scratch/`, `.swival/`, and the tasks-dir are preserved exactly as the untracked
   ones are. This fixes defect 4.

The caller `RelayDriver.Stage5.cs` (`HandleStage5Async`) must honor a failure result: if the discard
reports failure, flag the stage (do not proceed into stage 6 on an unverified worktree). Keep the
ledger note surfacing the RAW discarded paths.

## Tests (extend `WorktreeFilterTests` / `RelayDriverStage5Tests`)

The existing 16 tests cover only happy paths and miss every dangerous branch. Add fast tests (real
`git init`/`commit` per test, no sleeps) proving:

- A `+`-prefixed new testFile (`"+tests/NewTests.cs"`) is **preserved**, not deleted.
- A `testFiles` entry with a backslash and one with a `./` prefix both preserve their on-disk test.
- A case-divergent testFiles entry preserves the on-disk file on a case-insensitive host.
- A staged rename (`git mv`) during stage 5 still reverts the **other** non-test production edits
  (the batch does not abort), and the result reports success only when the worktree is actually clean.
- A tracked edit to a file under the tasks-dir and one under `.relay/` are **preserved** (not reverted).
- A failing/timed-out enumeration or revert surfaces a failure result and does NOT report a clean
  discard (inject a git failure or a non-repo root).

## Done when

- All six defects are fixed in `WorktreeFilter.cs`, with the caller honoring a failure result.
- The new tests above pass and the existing 16 `WorktreeFilter`/`RelayDriverStage5` tests stay green.
- The full suite passes; no test introduces a sleep or long timeout.
- The change stays general (no project-specific paths/extensions hard-coded).
