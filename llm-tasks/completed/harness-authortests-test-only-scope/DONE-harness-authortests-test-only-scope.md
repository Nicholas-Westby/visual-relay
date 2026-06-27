# Stage 5 Author-tests oversteps into production files and silently drops authored test files

Two coupled defects were observed on real runs (tasks 08 and 05):

- **Overstep (task 08):** stage 5's write scope is `"all"` (`RelayStages.cs:13`) — the agent
  edits production files in addition to test files. The red-gate stashes only the
  manifest's *implementation* files (`RedGate.ComputeStripSet`: everything in manifest that is
  not in `testFiles`), runs the test command, then `RestoreStashAsync` puts those
  implementation edits back. Stage 6 (Implement) then sees a half-done worktree, re-audits, and
  redoes work — inverting TDD and wasting 6–20 turns.

- **Silent drop (task 05):** `testFiles` from stage 5's JSON contract is only used ephemerally
  in `RelayDriver.cs:139–188` to build the gate command and call `AuthorTestGate.RunAsync`. The
  in-memory `manifest` is **never modified** by stage 5. If the agent authors a new test file
  that the stage-4 plan did not list in `manifest`, that file lives on disk but is absent from
  `manifest.txt`. `GitCommitter.CommitAsync` calls `git add -A` against `manifest` entries and
  `git add -u` for tracked modifications; a brand-new untracked file that is not in `manifest`
  reaches the commit only via the `preRunUntracked` diff auto-include path
  (`GitCommitter.cs:110–120`), which is a best-effort safety net, not a guarantee —
  `FindUncommittedAuthoredFilesAsync` (`RelayDriver.CommitGate.cs:170–178`) catches the miss
  post-commit but then flags and rolls back rather than committing. The agent believed its
  regression tests landed; they were silently dropped.

## Current state (researched)

### Stage 5 definition and write scope

`RelayStages.cs:13`:
```
Stage(5, "Author-tests", "balanced", "all", "all", """{ "testFiles": string[], "rationale": string }""")
```
The fourth parameter (`files`) is `"all"` — the agent has unconstrained write access. The fifth
parameter (`commands`) is also `"all"`. Compare stage 2 Research (`"some"`, read-only tools) or
stage 4 Plan (`"some"`, read-only tools). Stage 5's system prompt is:

`RelayStages.cs:45`:
> "Write tests for the target behavior only. They must fail before implementation."

The instruction says "tests only" but the scaffolding enforces nothing — `files = "all"` is
wired the same as stage 6 Implement.

### What the red-gate stashes and restores

`RedGate.ComputeStripSet` (`RedGate.cs:14–18`):
```csharp
public static IReadOnlyList<string> ComputeStripSet(IReadOnlyList<string> manifest, IReadOnlyList<string> testFiles)
{
    var tests = testFiles.ToHashSet(StringComparer.Ordinal);
    return manifest.Where(file => !tests.Contains(file)).ToArray();
}
```
Only files that appear in the **stage-4 manifest** *and* are not in `testFiles` are stashed.
Any production-file edit the agent made to a file that is *not* in the manifest is invisible to
`ComputeStripSet` — it is never stashed, so after `RestoreStashAsync` it stays in the worktree.
Even for manifest files, only *dirty* ones are stashed (`RedGate.cs:42–51`). After the test
run, `RestoreStashAsync` (`RedGate.cs:85–111`) runs `git checkout -- .` then `git stash apply`
to bring the implementation edits back. Stage 6 therefore starts with those edits present and
re-does them.

The gate correctly detects red (tests fail without implementation). The bug is not in the
gate logic — it is that non-test edits are never removed before stage 6 proceeds.

### testFiles are not merged into the manifest

`RelayDriver.cs:139–188`: at stage 5, `testFiles` is extracted from the agent's JSON, used to
build the gate command and passed to `AuthorTestGate.RunAsync`, then falls out of scope. The
`manifest` list (set at stage 4, `RelayDriver.cs:114–137`) is never modified by stage 5.

`RelayDriver.CommitGate.cs:159`: `GitCommitter.CommitAsync` is called with the same unmodified
`manifest`. A brand-new test file authored by stage 5 that was *not* already in the stage-4
manifest is an untracked file. `GitCommitter.cs:101–120` has an auto-include path that diffs
`preRunUntracked` against the current untracked set and stages new authored files — this covers
the success path. But `FindUncommittedAuthoredFilesAsync` (`RelayDriver.CommitGate.cs:170–178`)
runs *after* the commit and flags remaining authored-but-uncommitted files, which triggers a
rollback. So: a new test file authored by stage 5 is at best committed via the auto-include
path (not guaranteed — the auto-include excludes `.gitignore`-d paths and internal artifacts
only, so it should work for typical test files), and at worst flagged post-commit and dropped.
The agent has no feedback loop telling it which files actually landed.

### The two compound effects

1. Stage 5 writes production code → red-gate strips manifest implementation files → restores
   them → stage 6 sees partial implementation → re-audits and redoes work → TDD is inverted.

2. Stage 5 writes a test file not in the stage-4 manifest → `testFiles` lists it → gate runs
   correctly → `manifest` never updated → `GitCommitter` commits only via the auto-include path
   (fragile) → post-commit check may flag and roll back → agent's regression tests are silently
   dropped.

## What to build

Write the failing test(s) first. All changes are in the VR harness itself; they must work for
any repo/language ("test files" is always derived from the task's declared `testFiles` list, not
hardcoded globs).

### 1. After stage 5 completes: discard (not stash-and-restore) all non-testFiles edits

After parsing the stage-5 agent output in `RelayDriver.cs:139`, before proceeding to record the
stage or running the author-test gate, reset the worktree to HEAD for every file that is **not**
in `testFiles`:

- Compute the non-test set: all files that are `git status --porcelain` dirty (modified,
  untracked, deleted) AND are not in `testFiles`. This is broader than `ComputeStripSet` — it
  catches production edits to files outside the manifest too.
- For tracked files: `git checkout -- <non-test-dirty-tracked>` to reset them.
- For new untracked files outside `testFiles`: delete them from disk.
- Record a ledger note listing what was discarded so the operator can see it.
- After this discard step, the worktree contains ONLY the test-file edits the agent
  declared. The existing `AuthorTestGate.RunAsync` call follows unchanged: it stashes the
  manifest's implementation files, runs the test command to confirm red, and restores them.
  Stage 6 Implement then starts from a clean base with only test edits present — exactly the
  TDD contract.

A new helper `WorktreeFilter.DiscardNonTestEditsAsync(rootPath, testFiles, cancellationToken)`
in `VisualRelay.Core/Execution/` is a natural home for this logic. It should:
- Use `git diff --name-only` and `git ls-files --others --exclude-standard` to enumerate dirty
  tracked and new untracked files respectively.
- Filter out any path that appears in `testFiles` (ordinal comparison).
- Run `git checkout -- <tracked-files>` for dirty tracked paths not in `testFiles`.
- Delete untracked paths not in `testFiles` and not under `.relay/` / `.relay-scratch/` /
  `.swival/` (mirror `GitCommitter`'s `InternalArtifactPrefixes`).
- Return a `WorktreeFilterResult` with lists of discarded tracked and deleted untracked files
  for the ledger note.

Wire it into `RelayDriver.cs` immediately after `var testFiles = ReadStringArray(json, "testFiles")` (line 141), before the `hasImpl` check and before `AuthorTestGate.RunAsync`.

**Tradeoff — compile stubs:** a test file may import a symbol that does not yet exist (new
interface, method, type). The test then fails to compile, which counts as "red" — a compile
failure is a legitimate red exit code, not a green. No production stub is needed for the
red-gate to pass. If a test legitimately requires a new production stub to compile at all (e.g.
the missing type is in a different assembly), the compile failure satisfies the red-check. Stage
6 (Implement) creates the real implementation. The discard of any production edits the agent
made during stage 5 is therefore safe: compile-red is still red. Include a comment in
`WorktreeFilter` and in the ledger note making this explicit.

### 2. After stage 5 completes: merge testFiles into manifest before any subsequent stage

After the discard step, merge `testFiles` into the in-memory `manifest` list in
`RelayDriver.cs`, immediately after step 1:

```csharp
// Merge stage-5 authored test files into the manifest so they are committed.
foreach (var tf in testFiles)
{
    if (!manifest.Contains(tf, StringComparer.Ordinal))
        manifest.Add(tf);
}
await WriteManifestAsync(taskDirectory, manifest, cancellationToken);
```

`WriteManifestAsync` is already called at stage 4 (`RelayDriver.cs:136`). Calling it again
here updates `manifest.txt` on disk so that any resume path (`RelayDriver.CommitGate.cs:34–37`
re-reads manifest from disk) also picks up the test files. From stage 6 onward, `manifest`
includes the authored test files, so `GitCommitter.CommitAsync` stages them via `git add -A`
against the (now updated) manifest entries — the deterministic path, not the best-effort
auto-include path.

Filter test files through the same task-dir guard already applied at stage 4 (`RelayDriver.cs:
119–135`): if a `testFile` path is under `config.TasksDir`, drop it and note it in the ledger.
Also apply the `.gitignore` pre-check guard already present in `GitCommitter.CommitAsync`
(`GitCommitter.cs:50–65`) — but that check runs at commit time and will surface the error
there, so no extra pre-check is required here.

### 3. (Optional, low-risk) Narrow stage 5's write scope to `"some"`

Change the `files` parameter for stage 5 in `RelayStages.cs:13` from `"all"` to `"some"`, and
narrow `commands` from `"all"` to the same read-only set used by Research/Diagnose/Plan
(`"git,ls,cat,grep,find,head,tail,wc,sort,uniq,cut,tr,awk,sed"`), then add write-file
tooling as a separate param for the subset of test files only.

This depends on how `BuildInvocation` translates the `files`/`commands` parameters into the
subagent prompt and tool list — if `"some"` means read-only and a separate mechanism grants
write access only to declared test files, this is the ideal enforcement. If the harness does
not yet have that granularity, items 1 and 2 above are sufficient: they enforce the constraint
in the driver after the fact, which is simpler and equally correct. Pursue item 3 only after
confirming how `BuildInvocation` maps these fields.

## Done when

- **Non-test edits don't leak into stage 6:** run a task where the stage-5 agent modifies a
  production file. After stage 5 completes and before stage 6 runs, `git status` in the target
  repo shows no changes to production (non-testFiles) paths. A unit test exercises
  `WorktreeFilter.DiscardNonTestEditsAsync` with a fake git runner that has dirty tracked and
  untracked non-test files: after the call, the tracked files are restored to HEAD and the
  untracked files are deleted, while testFiles paths are untouched.

- **Authored test files are committed:** run a task where stage 5 authors a test file not in the
  stage-4 manifest. After stage 5's merge step, `manifest.txt` in `.relay/<taskId>/` contains
  that file. The final `git add -A -- <manifest>` in `GitCommitter.CommitAsync` stages it. A
  unit test exercises the `RelayDriver` stage-5 path (via `RelayDriverTest` doubles) and asserts
  that `manifest` contains all entries from `testFiles` after stage 5, and that `manifest.txt`
  on disk is updated accordingly.

- **Red-check is preserved:** the existing `AuthorTestGate` / `RedGate` logic is not changed.
  Tests for `AuthorTestGate` and `RedGate` that currently pass continue to pass. A new
  integration test confirms: after `WorktreeFilter.DiscardNonTestEditsAsync`, if tests fail
  (red), the gate accepts the result; if tests pass and implementation was stashed, the gate
  flags it. Both outcomes remain exercisable.

- **Tests added to manifest don't re-trigger task-dir guard silently:** unit test for the
  stage-5 manifest-merge path verifies that a `testFile` path under `config.TasksDir` is
  dropped and noted in the ledger, matching the stage-4 guard behavior.

- **`./visual-relay check` exits 0** after all changes land. Files changed:
  - `src/VisualRelay.Core/Execution/WorktreeFilter.cs` (new, <100 lines)
  - `src/VisualRelay.Core/Execution/RelayDriver.cs` (~10 lines added at stage-5 block,
    `RelayDriver.cs:139–189`)
  - Test file(s) under `tests/VisualRelay.Tests/` covering `WorktreeFilter` and the
    stage-5 manifest-merge path (new or extended, collectively <200 lines)
  - `src/VisualRelay.Core/Execution/RelayStages.cs:13` (optional, item 3)

- **Conventional Commit subject candidates** (best first):
  - `fix(driver): discard stage-5 production edits and merge testFiles into manifest`
  - `fix(author-tests): constrain stage 5 to test-only writes and guarantee test file commit`
  - `fix(relay): stage-5 worktree filter and manifest merge prevent TDD inversion`
