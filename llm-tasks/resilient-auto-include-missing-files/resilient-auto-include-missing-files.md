# Make git auto-include resilient to missing files (TOCTOU race)

## Problem

Stage 11 (Commit) fails because the auto-include pass in `GitCommitter.CommitAsync`
blindly passes every path returned by `CaptureUntrackedSnapshotAsync` straight to
`git add --`.  The snapshot (`git ls-files --others --exclude-standard`) and the
`git add` call are not atomic: a file can disappear between the two, producing

    git add auto-include failed (git exit 128):
    fatal: pathspec '"llm-tasks/restarted-task-shows-two-active-stages/
    Screenshot 2026-07-01 at 9.59.05 AM.png"' did not match any files

The path contains a U+202F NARROW NO-BREAK SPACE between the time and "AM" —
that character is legitimate in macOS filenames and is emitted by the app's built-in
`ControlScreenshot` feature.  The current code has no `File.Exists` gate, so every
file that passes the filter goes straight into a single `git add -- <path> …`
invocation.  If any path is missing the whole commit fails.

Additionally, the `IsUnderTasksDir` helper uses `Path.GetFullPath` + `Path.Combine`
which on macOS can produce NFC-normalised paths while the filesystem delivers
NFD.  For U+202F there is no decomposition so this is not the active trigger,
but the check is fragile for any composed character that *does* differ.

## Root cause

1. **TOCTOU race** — `GitCommitter.cs` lines 130‑147 build `newAuthored` from
   `git ls-files --others` output, then unconditionally `git add --` the entire
   list.  There is no existence check between the snapshot and the add.

2. **Normalisation-sensitive directory guard** — `IsUnderTasksDir` (duplicated in
   `GitCommitter.Untracked.cs` and `WorktreeResetter.cs`) resolves relative
   paths through `Path.GetFullPath`, which may normalise NFC while macOS
   filesystem paths are NFD.  This can cause `StartsWith` to miss files that
   begin with the tasks-dir prefix.

## Fix

### 1.  Resilience in the auto-include loop (`GitCommitter.cs`  ← primary)

Before adding each candidate path to `newAuthored`, verify the file or directory
exists on disk:

```csharp
var full = Path.Combine(rootPath, path);
if (!File.Exists(full) && !Directory.Exists(full))
    continue;          // skip vanished file — don't fail the whole commit
```

Apply the same guard **before** the single-batch `git add` so that stale entries
cannot sneak in.  Open question: should we also try `git add --ignore-missing`
only after the existence check?  (Answer: no — `--ignore-missing` is a more
recent git option and would mask real problems.  Existence check alone is
sufficient and deterministic.)

### 2.  Deterministic tasks-dir exclusion (`GitCommitter.Untracked.cs`  ← secondary)

Add a **relative-path prefix** check *before* the full-path resolution.  This
totally avoids any normalisation concerns because the relative paths come from
the same `git ls-files` invocation:

```csharp
if (!string.IsNullOrEmpty(tasksDir) &&
    (relativePath == tasksDir ||
     relativePath.StartsWith(tasksDir + "/", StringComparison.Ordinal) ||
     relativePath.StartsWith(tasksDir + "\\", StringComparison.Ordinal)))
    return true;
```

Keep the existing full-path resolution as a fallback, but the relative check
serves as the fast, deterministic first line.  Apply the same change to both
`GitCommitter.Untracked.cs` and `WorktreeResetter.cs`.

### 3.  Tests

Add the following facts to `GitCommitterAutoIncludeTests.cs` (or a new partial
file `GitCommitterAutoIncludeTests.Resilience.cs`):

| Test | What it proves |
|------|---------------|
| `CommitAsync_SkipsVanishedFile_BetweenSnapshotAndAdd` | Auto-include does not fail when a file listed by `ls-files` is deleted before `git add`.  The commit still succeeds with only the extant files. |
| `CommitAsync_AutoIncludesFileWithUnicodeNarrowNoBreakSpace` | A newly authored file whose name contains U+202F is successfully auto-included (proves the path works end‑to‑end). |
| `CommitAsync_ExcludesTasksDirFileWithUnicodeInPath` | A file dropped under `llm-tasks/` whose path contains U+202F is excluded from auto-include (the tasks-dir guard works regardless of Unicode). |
| `CaptureUntrackedSnapshotAsync_FindsFileWithNarrowNoBreakSpace` | The snapshot helper itself correctly captures files with U+202F in the name (no filtering at the `ls-files` level). |

All tests must be **deterministic** — create and delete files with known names
inside a disposable `TestRepository`, never depend on pre-existing repo state.

## Constraints

- Never weaken, skip, or delete any existing test.
- The fix must work on macOS where the filesystem normalises to NFD and where
  the screenshot files are written with a U+202F character.
- The commit must still fail if a **real** error occurs (e.g. permission denied);
  only missing-file errors must be silently skipped.
- `FindUncommittedAuthoredFilesAsync` must continue to exclude tasks-dir files
  (the existing test `CommitAsync_ExcludesTasksDirFileFromAutoInclude_WhenCreatedMidRun`
  in `GitCommitterAutoIncludeTests.TasksDir.cs` must keep passing).
