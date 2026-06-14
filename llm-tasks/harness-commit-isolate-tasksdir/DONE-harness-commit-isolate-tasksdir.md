# Exclude tasks-dir files from the auto-include pass in GitCommitter

## Current state (researched)

The "tolerate task files added mid-run" feature is almost complete. The QUEUE
snapshot (`RelayQueueController.DrainAsync`) and the stage-4 manifest guard
already exclude the tasks dir correctly
(`src/VisualRelay.Core/Execution/RelayDriver.cs:122-126`,
`src/VisualRelay.Core/Execution/RelayDriver.Artifacts.cs:136-142`). One gap
remains: the commit's **auto-include pass** does not exclude the tasks dir.

**The gap — `GitCommitter.cs:110-129`:**

```
if (preRunUntracked is not null)
{
    var currentUntracked = await CaptureUntrackedSnapshotAsync(rootPath, cancellationToken);
    var newAuthored = new List<string>();
    foreach (var path in currentUntracked)
    {
        if (!preRunUntracked.Contains(path) && !IsInternalArtifact(path))
        {
            newAuthored.Add(path);   // ← tasks-dir file passes this filter
        }
    }
    if (newAuthored.Count > 0)
    {
        var addNew = await GitAsync(rootPath, ["add", "--", .. newAuthored], cancellationToken);
        ...
    }
}
```

A new `llm-tasks/<x>/<x>.md` file dropped after the run started is:
- untracked (not in `preRunUntracked`)
- not an internal artifact (`InternalArtifactPrefixes` at line 9 = `[".relay/", ".relay-scratch/", ".swival/"]` — no tasks dir)

So it passes the filter and gets staged into the **running task's commit**
(cross-task contamination). This is the residual gap from
`DONE-tolerate-task-files-added-mid-run.md`.

**Same gap in `FindUncommittedAuthoredFilesAsync` (`GitCommitter.cs:271-287`)**:
the post-commit "did we miss any authored file?" check uses the same
`!IsInternalArtifact` filter without a tasks-dir exclusion, so it would
incorrectly report a new tasks-dir file as a missed authored file and trigger a
flag on an otherwise-correct commit.

**`CommitAsync` signature (`GitCommitter.cs:18-27`) — confirmed:**

```csharp
public static async Task<GitCommitResult> CommitAsync(
    string rootPath,
    string taskId,
    string taskHash,
    IReadOnlyList<string> commitMessages,
    IReadOnlyList<string> manifest,
    IReadOnlyList<string> proofFiles,
    string? commitToken,
    IReadOnlySet<string>? preRunUntracked,
    CancellationToken cancellationToken)
```

`CommitAsync` does **not** currently receive `config` or `config.TasksDir`. It
also has no equivalent to `IsPathUnderDirectory`. The only exclusion logic is
`IsInternalArtifact` (line 258-265), which checks static prefix strings.

**The call site (`RelayDriver.CommitGate.cs:159`)** has `config` in scope
(`ExecuteCommitStageAsync` receives `RelayConfig config` at line 125):

```csharp
var commit = await GitCommitter.CommitAsync(rootPath, taskId, taskHash, chain, manifest, proofFiles, activeLockNonce, preRunUntracked, cancellationToken);
```

**`IsPathUnderDirectory` (`RelayDriver.Artifacts.cs:136-142`)** takes
`(string rootPath, string relativePath, string directoryName)` and returns
`true` if `relativePath` resolves under `directoryName` (case-insensitive,
`Path.GetFullPath`-normalized). This is the pattern the fix must mirror.

## What to build

Write the failing tests first (TDD).

**1. Add a regression test to `GitCommitterAutoIncludeTests.cs`.**

Add a new `[Fact]` named
`CommitAsync_ExcludesTasksDirFileFromAutoInclude_WhenCreatedMidRun`. The test:
- Creates a temporary repo with a committed `src/app.cs`.
- Takes a pre-run snapshot (empty).
- The "agent" modifies `src/app.cs` and creates `src/new-impl.cs` (genuinely
  authored), AND creates `llm-tasks/new-task/new-task.md` (dropped mid-run by
  the user).
- Calls `CommitAsync` with the new `tasksDir` param set to `"llm-tasks"` and
  `manifest = ["src/app.cs"]`.
- Asserts the commit succeeds, `src/new-impl.cs` IS in the commit, and
  `llm-tasks/new-task/new-task.md` is NOT in the commit.

Also update `FindUncommittedAuthoredFilesAsync` tests if the function gets the
same parameter (see step 4).

**2. Add `string? tasksDir` parameter to `CommitAsync`.**

In `src/VisualRelay.Core/Execution/GitCommitter.cs`, add `string? tasksDir`
as the last parameter before `CancellationToken cancellationToken`. Default to
`null` so existing callers with no tasks-dir knowledge remain correct without
change (backward-compatible for tests and any future callers).

Updated signature:

```csharp
public static async Task<GitCommitResult> CommitAsync(
    string rootPath,
    string taskId,
    string taskHash,
    IReadOnlyList<string> commitMessages,
    IReadOnlyList<string> manifest,
    IReadOnlyList<string> proofFiles,
    string? commitToken,
    IReadOnlySet<string>? preRunUntracked,
    string? tasksDir,
    CancellationToken cancellationToken)
```

**3. Apply the exclusion in the auto-include delta (`GitCommitter.cs:116`).**

In the `foreach` that builds `newAuthored`, add a tasks-dir guard after
`!IsInternalArtifact(path)`:

```csharp
if (!preRunUntracked.Contains(path)
    && !IsInternalArtifact(path)
    && !IsUnderTasksDir(rootPath, path, tasksDir))
{
    newAuthored.Add(path);
}
```

Add a private static helper:

```csharp
private static bool IsUnderTasksDir(string rootPath, string relativePath, string? tasksDir)
{
    if (string.IsNullOrEmpty(tasksDir))
        return false;
    var fullPath = Path.GetFullPath(Path.Combine(rootPath, relativePath));
    var dirFullPath = Path.GetFullPath(Path.Combine(rootPath, tasksDir));
    return fullPath.StartsWith(dirFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fullPath, dirFullPath, StringComparison.OrdinalIgnoreCase);
}
```

This mirrors `RelayDriver.Artifacts.cs:136-142` (`IsPathUnderDirectory`) exactly.

**4. Apply the same exclusion in `FindUncommittedAuthoredFilesAsync`.**

`FindUncommittedAuthoredFilesAsync` (`GitCommitter.cs:271-287`) performs the
same delta scan post-commit to detect missed authored files. Apply the same
`!IsUnderTasksDir` guard there so a new tasks-dir file is not reported as a
missed authored file and does not trigger a false flag. Add `string? tasksDir`
to this method's signature as well (nullable, default behavior: no exclusion).

**5. Thread `config.TasksDir` through the call site.**

In `src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs:159`, pass
`config.TasksDir` as the new `tasksDir` argument:

```csharp
var commit = await GitCommitter.CommitAsync(
    rootPath, taskId, taskHash, chain, manifest, proofFiles,
    activeLockNonce, preRunUntracked, config.TasksDir, cancellationToken);
```

Do the same for the `FindUncommittedAuthoredFilesAsync` call at line 170:

```csharp
var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
    rootPath, preRunUntracked, config.TasksDir, cancellationToken);
```

`config` is already in scope (`ExecuteCommitStageAsync` receives it at line 125).

## Done when

- The new regression test
  `CommitAsync_ExcludesTasksDirFileFromAutoInclude_WhenCreatedMidRun` is
  written first and **initially fails** (red), then passes after the fix
  (green).
- A file created under `config.TasksDir` after the run started is **not** staged
  or committed by the auto-include pass in `CommitAsync`.
- A genuine run-authored file outside the tasks dir (e.g. `src/new-impl.cs`) IS
  still auto-included as before (positive case asserted in the same test).
- `FindUncommittedAuthoredFilesAsync` does **not** report a new tasks-dir file
  as a missed authored file (covered by test or assertion in the same test body
  by confirming no false flag is triggered).
- All existing `GitCommitterAutoIncludeTests` still pass (`.relay/`, `.swival/`
  exclusions, pre-existing untracked exclusion, gitignored exclusion, null
  preRunUntracked no-op).
- `./visual-relay check` green.
- `GitCommitter.cs` stays under 300 lines.
- Conventional Commit subject (e.g.
  `fix(committer): exclude tasks-dir files from auto-include pass`).
