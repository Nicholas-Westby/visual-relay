# Task: Stop retrying hook-rejected commit candidates, and finish the fake-clock migration

`GitCommitter` retries a commit candidate that a commit-msg/pre-commit hook
just rejected, sleeping 250ms + 1s of real time before giving up on it —
retrying an identical rejected message is always futile. This burns ~1.25s per
rejected candidate in every production drain and in two tests. Fix the
product, fix the one test class that missed the fake-clock migration, and add
a guard so no future test calls `GitCommitter.CommitAsync` on the real clock.

### Evidence (2026-07-19 slow-test investigation)

- `src/VisualRelay.Core/Execution/GitCommitter.cs:206` — the per-candidate
  `git commit` attempt goes through `GitAsync` WITHOUT an `isSuccessExit`
  widening, so a hook rejection (exit 1) is treated as transient: retried 3×
  with 250ms + 1s backoff (`GitCommitter.cs:245-257`) before the candidate
  loop moves on.
- `GitCommitter.cs:239-242` documents the intended fix seam: "A caller widens
  this for a command whose non-zero exit is a normal result, not a transient
  failure — otherwise that result burns pointless backoff."
- Measured solo (VM, filtered runs, 2026-07-19):
  `GitCommitterRunBaseSquashGuardsTests.CommitAsync_WithRunBase_WhenAllCandidatesRejectedAfterSquash_RestoresOrigHead`
  = 2.54s (two rejected candidates × 1.25s real sleep) and
  `RelayDriverGitCommitTests.RunTaskAsync_CommitMsgHookRejectsFileNames_FallsBackToLaterCandidate`
  = 1.28s (one rejected candidate). For contrast, the already-migrated
  `GitCommitterHookRejectsFirstTests`/`GitCommitterHookRejectsAllTests`
  (see `GitCommitterHookRejectionTests.cs`, which inject `ManualTimeProvider`)
  measure 0.02s.
- `tests/VisualRelay.Tests/GitCommitterRunBaseSquashGuardsTests.cs:145-151` —
  the `CommitAsync` call passes no `timeProvider:`, so the backoff runs on
  `TimeProvider.System`.
- `src/VisualRelay.Core/Execution/RelayDriverDependencies.cs:23-32` —
  `ForTests` defaults `TimeProvider` to null, so driver-level commit tests
  also sleep for real when a candidate is rejected.

### What to build

Three focused commits, in this order:

1. **Product fix.** At the commit-attempt call (`GitCommitter.cs:206`) pass
   `isSuccessExit: static code => code is 0 or 1`. Exit 1 from `git commit`
   is a final per-candidate verdict (hook rejection, precondition failure);
   the candidate loop already falls through to the next message. Genuinely
   transient failures (e.g. `index.lock` contention) surface as exit 128 with
   a `fatal:` message and keep the existing retry/backoff. Do not touch the
   backoff schedule or `GitFailureClassifier` — the widening seam is the
   designed fix point.
   - Coverage mapping duty: check `GitCommitterProbeRetryTests` and
     `GitCommitterPersistentTimingTests` (in `GitCommitterResilienceTests.cs`).
     If their scripted failures use exit 1 for the *commit* step, change the
     scripted exit code to a retryable one (e.g. 128 without a deterministic
     message) so the retry path keeps its coverage — same scenarios, same
     assertions, name-by-name unchanged.
2. **Test fix.** Inject a `ManualTimeProvider` in
   `GitCommitterRunBaseSquashGuardsTests` exactly the way
   `GitCommitterHookRejectionTests.cs` does (advance-pump loop pattern),
   passing `timeProvider:` to every `CommitAsync` call in the class. After
   commit 1 the sleeps are gone anyway; this makes the class immune to any
   future retryable path.
3. **Guard.** New guard-as-test rule: inside `tests/VisualRelay.Tests`, any
   direct invocation of `GitCommitter.CommitAsync(` must pass a
   `timeProvider:` named argument. Implement as
   `tools/VisualRelay.Guards/TestClockInjectionGuard.cs` (pure matcher,
   mirror the structure and self-exemption style of
   `RealGitFallbackGuard.cs`) plus a test-side runner
   `TestClockInjectionGuardTests` that scans via the shared
   `CachedSyntaxTreesFixture` (pattern: `FakeClockGuardTests`). Empty
   allowlist. Driver-level tests that reach commits through
   `RelayDriverDependencies.ForTests` are explicitly out of scope for the
   guard (that seam-wide migration is a separate effort); the guard covers
   the direct-call slip that actually happened.

### Constraints

- Production behavior change is exit-1-fail-fast only; all other retry
  behavior (probe retries, exit-128 transients, backoff timings) unchanged.
- Coverage is non-negotiable: no test deleted, skipped, or weakened; any test
  whose scripted exits change carries a name-by-name mapping in the run
  summary showing the scenario still covered.
- Keep files under the 300-line guard.

### Tests (red first)

- New `GitCommitter` unit test: scripted invoker returns exit 1 for the
  commit step with two candidates → the second candidate is attempted with
  ZERO delay calls observed on an injected `ManualTimeProvider` (assert no
  timer was ever scheduled, or that virtual time never advanced).
- Existing retry tests still green after the exit-code adjustment in step 1's
  mapping.
- Guard test: a synthetic source with a bare `GitCommitter.CommitAsync(...)`
  call (no `timeProvider:`) yields exactly one violation; the repo tree scan
  yields zero.

### Verification

- `./visual-relay check` fully green.

### Commit-message evidence

Measure before and after while implementing (the two named tests, solo runs),
then put one filled-in evidence bullet in the commit message body, following
the attached `commit-message-evidence.md`. Never pre-fill that bullet —
numbers are measured at implementation time and go into the eventual commit
message, nowhere else.
