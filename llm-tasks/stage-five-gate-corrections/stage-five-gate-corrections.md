# Three corrections around the stage-5 gate: the red re-ask, rooted paths, attempt numbers

The author-test gate landed with its evidence intact, and three reviews of it
stand open. First, a decision the code makes without saying so: when the gate
already recorded `red` and the diff audit reports an implementation hunk, the
stage is re-asked anyway, at the price of a whole second stage-5 attempt.
Second, a path a model writes as absolute is "normalized" by trimming its
leading `/` into a repo-relative name that exists nowhere, then dropped from
the strip set without a word. Third, `verify_result.attempt` is a stage
attempt for stage 5, a loop ordinal for stage 11 and a literal `1` for stages
10 and 12, while the trace directories count every invocation; on a resume the
two diverge and an earlier run's verify output is overwritten. This task pins
the first decision with its reason, makes rooted paths resolve or report, and
gives every gate one attempt rule.

## Evidence

- Review of 2026-09-11, items 5, 7 and 8.
- `Execution/RelayDriver.Stage5Audit.cs:69-84` `ResolveAuthorTestReask`: the
  gate's re-ask wins, else the audit's, with no reading of `outcome.Check`;
  `AuthorTestGateOutcome.ForRun` (`Execution/AuthorTestGateOutcome.cs:118-119`)
  returns `Red` with `ReaskRequested: false`. The retired spec required "any
  hunk reported triggers the same single re-ask"; the code comment covers
  precedence, not color. `RelayDriver.Stage5.cs:130-131`: "compile failures
  count as red", so a red earned by a hunk that does not compile is not a
  proof the tests fail for the right reason.
- `Execution/WorktreeFilter.cs:32-42` `NormalizeRepoRelativePath`: `+`, `\`,
  `./`, then `TrimStart('/')`, `TrimEnd('/')`. `/Users/me/repo/src/x.rs`
  becomes `Users/me/repo/src/x.rs`. `RelayDriver.Stage4.cs:52-59` feeds every
  manifest entry through it; `RedGate.ComputeStripSet` (ff01195d) normalizes
  both sides the same way, and `RedGateTests.StripToRedAsync_SkipsAbsentPathsAndRestoresTheStash`
  (`:8`) pins that an absent path is skipped in silence.
- `Execution/RelayDriver.VerifyFix.cs:63-98`: `for (var run = 1; …)`, each
  run builds its invocation through `BuildInvocation` (`:95-96`), which
  allocates `RelayAttempt.Next(taskDirectory, stage.Number)`
  (`RelayDriver.Invocation.cs:30`, the highest existing `stage11-attempt*`
  plus one, `Traces/RelayAttempt.cs:42-60`); the gate is then published with
  `attempt: run` (`:169`, `:189`) and its output persisted as
  `stage11-attempt{run}.verify-output.txt` (`RelayDriver.VerifyObservability.cs:101-113`).
  Stage 10 publishes `attempt: 1` (`RelayDriver.cs:212`, `RelayDriver.Stage9.cs:59-60`)
  before `BuildStageInvocation` allocates the model attempt (`:156`); stage
  12 publishes `attempt: 1` (`RelayDriver.CommitGate.cs:46,54`). Stage 5 reads
  `AttemptOf(invocation.ReportFile)` (`RelayDriver.Stage5.cs:64-68`,
  `Stage5Audit.cs:110-111`). `docs/relay-artifacts.md:43` says "`Attempt` is
  the stage attempt, so a re-ask leaves two", which is true of stage 5 only.

## Current state (researched at d5bf93cc)

- Stage 5's pass (`RelayDriver.Stage5.cs:104-206`): normalize `testFiles`
  (`:125`), filter, merge into the manifest (`:158-181`), scope check, audit
  unless `reaskUsed` (`:188-191`), gate (`:192-193`), publish, then
  `ResolveAuthorTestReask` (`:205`). `RunStage5WithReaskAsync` (`:47-98`)
  performs at most one re-ask per stage run.
- `RelayAttempt.TryParse`'s regex (`:62-63`) matches trace directories,
  report files and verify-output files alike, so `Next` already counts the
  verify artifacts a gate writes.
- Tests: `RelayDriverStage5AuditTests` (nine facts; `:81` a hunk re-asks
  once, `:115` gate and audit re-ask once), `RelayDriverStage5ReaskTests`
  (`:59` "one verify_result per gate run, each naming the attempt it belongs
  to"), `RelayDriverManifestPrefixTests` (three facts), `RedGateTests`
  (three), `RelayDriverVerifyFixOutputTests`, `RelayDriverResumeCommitGateVerifyTests`.

## Prescribed approach

1. The red re-ask stays, and says why. A reported hunk buys the re-ask
   whatever the gate recorded: red proves the targeted command failed, not
   that the tests are tests, and a hunk that changes behavior inside a
   declared test file is exactly what stage 6 must not inherit and what the
   worktree filter can never revert. `ResolveAuthorTestReask`'s comment gains
   that sentence; `docs/OPERATIONS.md:133-136` gains "including when the gate
   is already red: a red earned by a half-implementation that does not
   compile is not a proof". The cost is bounded by `ShouldRun`: on `auto` the
   audit fires only where inline-capable or suspect files were edited.
2. Rooted paths resolve or report. `Execution/ManifestPaths.cs`:
   `internal static bool TryResolve(string rootPath, string entry, out string
   repoRelative, out string? rejection)`: after the backslash swap, an entry
   that `Path.IsPathRooted` (or starts with `/`) is made full and compared
   Ordinal to `Path.GetFullPath(rootPath)`; under the root it becomes the
   relative path and continues through `NormalizeRepoRelativePath`; outside it
   fails with `absolute path outside the workspace`. `NormalizeRepoRelativePath`
   drops its `TrimStart('/')` so a rooted path can never pass as relative.
   Both entry points use it: stage 4 (`Stage4.cs:52-59`) and stage 5 (`:125`,
   through `NormalizeTestFileList` taking the root). A rejected entry is
   dropped with a ledger note (`> **Note**: dropped 1 entry from manifest
   (absolute path outside the workspace): …`, the shape of `:62-65`) and a
   warn event `path_entry_dropped` with `entry` and `reason`, stage 4 or 5 as
   the event's stage. During stages 1-4 the root is the planning worktree, so
   a model that spells the worktree's absolute path gets the right relative one.
3. One attempt rule: a gate's attempt is the stage invocation it belongs to.
   `BuildInvocation` gains `int? attempt = null` (`attempt ?? RelayAttempt.Next(…)`).
   Stage 11 passes `AttemptOf(invocation.ReportFile)` to `RunIsolatedVerifyAsync`
   and `PublishVerifyResultAsync` in place of `run`; `run` stays the ordinal
   `stage_escalated` reports. Stage 10 allocates `RelayAttempt.Next(taskDirectory, 10)`
   before its pre-agent gate, publishes with it and hands it to
   `BuildStageInvocation`, so the gate and the model attempt share a number.
   Stage 12 allocates `RelayAttempt.Next(taskDirectory, 12)` per commit-gate
   run, which is monotonic because the verify artifacts count. Nothing is
   overwritten on a resume any more. `docs/relay-artifacts.md:43` reads:
   "`Attempt` is the attempt of the stage invocation the gate belongs to: a
   re-ask (5) or an escalation (11) leaves one record per attempt, stage 10's
   pre-agent gate carries the number the stage then runs as, and each
   commit-gate run (12) takes a fresh one." TROUBLESHOOTING.md's entry on
   reading verify output gains the same sentence where it names the file.

## Tests

- `RelayDriverStage5AuditTests.Stage5_RedGateWithAReportedHunk_StillReasksOnce`:
  pass one is red and the audit reports a hunk; exactly one re-ask; the
  second pass is gated and its check is the one recorded; two `verify_result`
  events with attempts 1 and 2.
- `ManifestPathsTests`: `TryResolve_ARootedPathUnderTheRoot_IsMadeRelative`
  (`/repo/src/x.rs` with root `/repo` gives `src/x.rs`; a Windows drive form
  through the injected separator swap likewise); `TryResolve_ARootedPathOutsideTheRoot_IsRejectedWithTheReason`;
  `TryResolve_ARelativePath_IsNormalizedAsBefore` (`+`, `./`, `\`, trailing `/`);
  `NormalizeRepoRelativePath_KeepsALeadingSlash`.
- `RelayDriverManifestPrefixTests.Stage4_AbsoluteEntryUnderTheRoot_BecomesRepoRelative`
  and `Stage4_AbsoluteEntryOutsideTheRoot_IsDroppedAndReported` (ledger note
  and `path_entry_dropped` with the reason); `RelayDriverStage5GateTests.Stage5_AbsoluteTestFile_IsResolvedLikeTheManifest`;
  `RedGateTests.ComputeStripSet_NeverHoldsARootedPath`.
- `RelayDriverVerifyFixOutputTests.FixVerify_OnResume_NumbersItsGateAfterTheEarlierAttempts`
  (seed `stage11-attempt1/` and `stage11-attempt2.report.json`; the loop's
  first `verify_result` carries attempt 3, `stage11-attempt3.verify-output.txt`
  is written and `stage11-attempt1.verify-output.txt` is untouched);
  `FixVerify_TheEscalationEvent_StillCountsRuns` (`stage_escalated` run 2 of 3
  while the gate says attempt 4). `RelayDriverStage9Tests` (or the class that
  drives stage 10): `Stage10PreAgentGate_SharesItsAttemptWithTheModelRun`.
  `RelayDriverResumeCommitGateVerifyTests.CommitGate_TakesAFreshAttemptPerRun`.

## Verification (through the control API)

Clone gorilla/mux as `mux.prep.md` describes, `open-folder`, `bootstrap`,
patch `testCmd`, `create-task` one validation task, `run-selected`; `cancel`
it during stage 6 (poll `/state.runningTasks[0].stageNumber`), then `resume`
and wait for `isBusy` false. Then:

    ls <clone>/.relay/<task>/ | grep -E 'stage1[01]-attempt' | sort
    grep verify_result <clone>/.relay/<task>/run.log | jq -c '{s: .stage, a: .attempt, cmd: .data.command}'
    grep -c path_entry_dropped <clone>/.relay/<task>/run.log

Expected: every `verify_result` for stages 10 and 11 names an attempt whose
`stage{N}-attempt{k}` directory or report exists, the highest stage-11 attempt
in the listing equals the last event's attempt, no `stage11-attempt1.verify-output.txt`
was rewritten after the resume (compare its `capturedUtc` header with the
cancel time), and the dropped-entry count is 0 on a clean run. For item 2,
edit one task file to ask the model to list the file under its absolute path
in the plan and confirm the manifest holds the relative name. Put the
attempt table in the commit body.

## Out of scope

Hunk-level removal of implementation from a test file; the stage-5 re-ask
message; `RelayAttempt`'s regex; stage 9's own numbering (it has no gate of
its own); the manifest add in the commit stage.

## Rejected alternatives

- Suppress the audit's re-ask on a red gate, as the review proposed: it
  trades one stage run for an implementation hunk stage 6 inherits, and the
  red it trusts may be a compile failure the hunk caused.
- Keep trimming the leading `/` and warn: a warning about a path that has
  already been mangled cannot name the file the model meant.
- Number every gate by a per-stage counter of its own: a third numbering
  beside the trace directories and the loop ordinal, when the invocation's
  number is already on disk and already what stage 5 reports.
