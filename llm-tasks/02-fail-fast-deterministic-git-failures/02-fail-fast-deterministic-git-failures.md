# Fail fast on deterministic git failures in retry backoff

`PlanningWorktree.RunGitAsync` (src/VisualRelay.Core/Execution/PlanningWorktree.cs)
and `GitCommitter.GitAsync` (src/VisualRelay.Core/Execution/GitCommitter.cs)
retry ANY nonzero git exit 3x with real-time backoff (250ms, then 1s). That is
right for transient failures (`index.lock` races) but wrong for deterministic
ones: exit-128 `fatal: not a git repository` can never succeed, yet every
isolated verify against a non-git root sleeps the full 1.25s before the driver
falls back in-place. Three driver paths pay it: the stage-10 pre-agent gate
(RelayDriver.Stage9.cs), EVERY verify-fix attempt (RelayDriver.VerifyFix.cs),
and commit-gate resume revalidation (RelayDriver.CommitGate.cs). Real git,
`GitSim`, and `NullGitInvoker` all emit exit 128 with the identical
"not a git repository" message, so one classifier behaves the same everywhere.

## Measured cost (2026-07-18, Tart VM)

- One 12-stage happy-path driver run: ~1.4s, of which ~1,258ms is the stage-10
  retry sleep. `RunTaskAsync_NormalRerun_StartsFromStage1` (two runs) = ~2.8s
  isolated, ~90% asleep; 13s on the host inside the parallel suite.
- ~40 driver-test files pay this per pipeline run; verify-fix tests pay it per
  attempt. The host's full suite ran at ~17% CPU — sleep-dominated.

## Prescribed approach

Classify by message signature, not exit code alone — git uses 128 for transient
`index.lock` failures too.

### Steps

1. Add `GitFailureClassifier` (new file in src/VisualRelay.Core/Execution):
   `public static bool IsDeterministic(int exitCode, string output)` — true iff
   exit is nonzero AND the output contains, ordinal-ignore-case,
   `not a git repository` or `invalid reference`. Nothing else. XML-doc that the
   set is deliberately conservative: unknown failures stay retryable.
2. `PlanningWorktree.RunGitAsync`: after a failed attempt, when
   `IsDeterministic` — throw the same `InvalidOperationException` immediately;
   no delay, no further attempts. Leave the exception-path retry (process start
   failure) untouched.
3. `GitCommitter.GitAsync`: same check — return the failed result immediately
   (its contract returns results rather than throwing). Keep the
   `isSuccessExit` widening used by the check-ignore probe.
4. Thread a clock through the driver so tests can prove "no sleep happened":
   add `TimeProvider? TimeProvider = null` as the last positional of the
   `RelayDriverDependencies` record (and a pass-through parameter on
   `ForTests`). Pass `_dependencies.TimeProvider` at the four call sites that
   omit it today: `PlanningWorktree.CreateAsync` (RelayDriver.VerifyWorktree.cs),
   `PlanningWorktree.RemoveAsync` (RelayDriver.VerifyWorktreeCleanup.cs), and
   the `GitCommitter.CommitAsync` + `FindUncommittedAuthoredFilesAsync` calls in
   RelayDriver.CommitGate.cs. `ForTests` keeps the null default — real time.
5. Tests (tests/VisualRelay.Tests):
   - Classifier unit tests: both signatures deterministic; exit 0 never; an
     `index.lock` message and an unknown fatal both retryable.
   - `PlanningWorktree.CreateAsync` with an UNREGISTERED `GitSimEngine` and
     `timeProvider: new ManualTimeProvider()`: capture the returned task without
     awaiting and assert it is already faulted — GitSim completes synchronously,
     so any regression back to sleeping leaves the task pending and fails the
     assert instantly instead of hanging.
   - `GitCommitter` deterministic failure via the existing
     `GitCommitterResilienceTests` shim pattern: exactly one invocation, and a
     `ManualTimeProvider` that is never advanced. Existing transient-retry
     tests must stay green and still see 3 attempts.
6. Add the overreach guard `FakeClockGuard` (matcher in tools/VisualRelay.Guards
   + `FakeClockGuardTests` consuming `CachedSyntaxTreesFixture`), enforcing over
   `src/` and `tools/` trees: no `ManualTimeProvider` or `FakeTimeProvider`
   identifier ever appears; every `TimeProvider`-typed parameter default is
   `null` (bodies resolve via `?? TimeProvider.System`); and no src csproj
   references a time-testing package. Fake clocks are a tests-only seam —
   production must default to real time.
7. Update the two stale comments that document the burn this task removes
   (search tests for "250ms + 1s" — RewriteMutualExclusionTests and
   ControlApiConfirmGatedTests reference the ~2.5s retry cost).
8. Confirm coverage with `./visual-relay audit retry-delay-loops` (task 01):
   the report must show both retry loops consulting the classifier.

### Guardrails

- Do NOT change attempt counts or backoff timing for transient failures.
- Do NOT widen the deterministic set beyond the two signatures.
- Do NOT default `ForTests` (or anything in src/) to a fake clock, and do not
  auto-advance time anywhere — a test that wants virtual time passes
  `ManualTimeProvider` explicitly. The new guard exists to keep it that way.

## Done when

Full suite green; new classifier/fail-fast/guard tests pass; driver-family
test times drop measurably (re-measure, see evidence sheet).

## Commit-message evidence

Measure at implementation time per the attached evidence sheet; the numbers go
in the commit body, never in this file.
