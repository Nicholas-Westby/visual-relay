# A plan path written as absolute resolves to the repository or is reported

The plan stage lists the files a task will touch, and the author-tests stage
lists the test files it wrote. Both lists are repo-relative by contract. When
a model writes an absolute path instead, VR trims the leading `/` and carries
on with a relative name that exists nowhere: `/Users/me/repo/src/x.rs`
becomes `Users/me/repo/src/x.rs`. The file then quietly drops out of
everything that reads the list: a new file is skipped when the commit stages
the manifest (`GitCommitter.Manifest.cs:28-39` passes over a path that does
not exist), an implementation file is not stripped by the red gate, a test
file is not counted as one. Nothing is logged. This task
makes an absolute path under the workspace resolve to the relative name the
model meant, and makes any other absolute path a reported drop.

This spec replaces `stage-five-gate-corrections` (2026-09-11), which had three
items. The attempt numbering was fixed on 2026-09-14 (bc714feb, pinned by
`VerifyOutputAttemptTests`); one documentation sentence is left and rides
along here. The "re-ask on a red gate" decision needed no change:
`RelayDriverStage5AuditTests.Stage5_AuditReportsAHunk_ReasksExactlyOnce`
already drives a red gate and asserts the single re-ask.

## Evidence

- Review of 2026-09-11, item 7.
- `Execution/WorktreeFilter.cs:32-42` `NormalizeRepoRelativePath`: strips a
  leading `+`, swaps `\` for `/`, strips `./`, then `TrimStart('/')` and
  `TrimEnd('/')`. A rooted path comes out as a relative path to nowhere.
- `RedGateTests.StripToRedAsync_SkipsAbsentPathsAndRestoresTheStash` pins that
  an absent path is skipped in silence, so the mangled name never surfaces.

## Current state (researched at f8a0d07b)

- Three readers take a model-written path list:
  - stage 4's manifest, `Execution/RelayDriver.Stage4.cs:50-59`: each entry is
    dropped when it lies under the tasks directory (with a ledger note,
    `:62-69`), else normalized through `NormalizeRepoRelativePath`;
  - the plan-completeness retry, `Execution/RelayDriver.Snapshot.cs:138-143`:
    the same tasks-directory filter, then only the `+` is stripped, with no
    normalization at all;
  - stage 5's `testFiles`, `Execution/RelayDriver.Stage5.cs:125`, through
    `WorktreeFilter.NormalizeTestFileList` (`WorktreeFilter.cs:51-52`).
- `RedGate.ComputeStripSet` (`Execution/RedGate.cs:25-31`) normalizes both
  lists the same way before it subtracts one from the other.
- `RelayDriver.Artifacts.cs:170-176` `IsPathUnderDirectory` already shows the
  shape of a root-anchored comparison (`Path.GetFullPath` on both sides).
- During stages 1 to 4 the root is the planning worktree, so an absolute path
  a model copies from its own tool output names the worktree, not the
  checkout.
- `docs/relay-artifacts.md:43` still says of `verify_result`: "`Attempt` is
  the stage attempt, so a re-ask leaves two", which since bc714feb is true of
  stage 5 only.

## Prescribed approach

1. `Execution/ManifestPaths.cs`: `internal static bool TryResolve(string
   rootPath, string entry, out string repoRelative, out string? rejection)`.
   After the `+` strip and the backslash swap, an entry that is rooted
   (`Path.IsPathRooted`, a leading `/`, or a drive form such as `C:/`) is made
   full and compared with `Path.GetFullPath(rootPath)`: under the root it
   becomes the relative path and continues through
   `NormalizeRepoRelativePath`; outside it the call fails with `absolute path
   outside the workspace`. A relative entry is normalized as today.
2. `NormalizeRepoRelativePath` stops trimming the leading `/`, so a rooted
   path can never pass as relative again. Its doc comment says so.
3. All three readers go through `TryResolve`: stage 4, the completeness retry
   (which gains the normalization it lacks) and stage 5's `testFiles`
   (`NormalizeTestFileList` takes the root). A rejected entry is dropped with
   a ledger note in the shape of the existing one (`> **Note**: dropped 1
   entry from manifest (absolute path outside the workspace): ...`) and a warn
   event `path_entry_dropped` with `entry` and `reason`, carrying the stage
   that read it.
4. `docs/relay-artifacts.md:43`: "`Attempt` is the attempt of the stage
   invocation the gate belongs to: a re-ask (5) or an escalation (11) leaves
   one record per attempt, stage 10's gate carries the number the stage then
   runs as, and each commit-gate run (12) takes a fresh one." The same table
   gains the `path_entry_dropped` row.

## Tests

- `ManifestPathsTests`: `TryResolve_ARootedPathUnderTheRoot_IsMadeRelative`
  (`/repo/src/x.rs` with root `/repo` gives `src/x.rs`; the drive form
  likewise, through an injected platform flag so it runs on macOS);
  `TryResolve_ARootedPathOutsideTheRoot_IsRejectedWithTheReason`;
  `TryResolve_ARelativePath_IsNormalizedAsBefore` (`+`, `./`, `\`, trailing
  `/`); `NormalizeRepoRelativePath_KeepsALeadingSlash`.
- `RelayDriverManifestPrefixTests`:
  `Stage4_AbsoluteEntryUnderTheRoot_BecomesRepoRelative` and
  `Stage4_AbsoluteEntryOutsideTheRoot_IsDroppedAndReported` (the ledger note
  and `path_entry_dropped` with the reason); the completeness retry's manifest
  gets the same two facts.
- `RelayDriverStage5GateTests.Stage5_AbsoluteTestFile_IsResolvedLikeTheManifest`;
  `RedGateTests.ComputeStripSet_NeverHoldsARootedPath`.
- Watch each fail first; mutate `TryResolve` to accept every rooted path and
  confirm the outside-the-root fact catches it.

## Verification (through the control API)

Clone gorilla/mux into a scratch folder, `open-folder`, `bootstrap`, set
`testCmd` to `go test -count=1 ./...` in `.relay/config.json`, `refresh`, and
create one small task (for example a new route matcher with its test) whose
text asks the plan to list its files by absolute path. `run-selected`, wait
for `isBusy` false, then:

    cat <clone>/.relay/<task>/manifest.txt
    grep -c path_entry_dropped <clone>/.relay/<task>/run.log
    git -C <clone> show --stat HEAD | head

Expected: the manifest holds repo-relative names only, the dropped count is 0
(every absolute path was under the worktree), and the commit contains the
files the plan named. Put the manifest and the count in the commit body.

## Out of scope

Hunk-level removal of implementation from a test file; the stage-5 re-ask
message; `RelayAttempt`'s numbering (done in bc714feb); the manifest add in
the commit stage.

## Rejected alternatives

- Keep trimming the leading `/` and warn: a warning about a path that has
  already been mangled cannot name the file the model meant.
- Reject every absolute path: the model usually means a real file under the
  root, and resolving it costs one comparison.
