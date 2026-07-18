# Eliminate real-git process default in ForTests (pipeline test speed)

When `RelayDriverDependencies.ForTests()` is called without a `gitInvoker`
parameter, it defaults to `new GitInvoker()` — spawning real git subprocesses
for every git operation across all 11 pipeline stages. About 60 test call sites
fall into this trap, and those are precisely the 30–52 second tests in the
baseline (see
`llm-tasks/completed/speed-up-automated-tests-july-17/timings-baseline.txt`).

## Measured cost

From the baseline timings file — the slowest 50 tests are all
`RelayDriver*` pipeline tests at 30–52 s each. Call sites that already route
through `RelayDriverTestHelpers.DepsFor()` (GitSim-backed) are among the faster
pipeline tests at 5–8 s each. The delta is the real-git process floor.

## Prescribed approach

Change the **default** value of the `gitInvoker` parameter in
`RelayDriverDependencies.ForTests()` from `null` → `new GitInvoker()` to
**`new GitSimEngine()`** (an in-memory, no-process Git simulator that already
exists at `tests/VisualRelay.GitSim/GitSim.cs`).

Most callers need zero changes — they never inspect the invoker. For the few
tests that genuinely exercise real-git commit paths and MUST have a real repo,
leave them with explicit `new GitInvoker()` and move them behind an opt-in
slow/integration tag.

### Steps

1. In `src/VisualRelay.Core/Execution/RelayDriverDependencies.cs`, change the
   default for the `gitInvoker` parameter so that
   `ForTests(subagent, testRunner, eventSink)` yields `GitSimEngine`, not
   `GitInvoker`. A `GitSimEngine` with a deleted/null root answers every git
   probe with `fatal: not a git repository` — matching the real binary's
   behavior on a non-repo temp directory.

2. Find every direct `ForTests(...)` call that currently relies on real git
   behavior (search: `RelayDriverDependencies.ForTests(` without `sim` or
   `new GitSimEngine` in the argument list). Most of these should be fine with
   the new default — they don't assert git outcomes. The ones that do must pass
   an explicit `new GitInvoker()`.

3. The `RealGitIntegrationDriverTests` class (and any test that needs a real
   git repo) must either:
   - Pass an explicit `new GitInvoker()` to `ForTests`, or
   - Use `ForTests(..., gitInvoker: new GitInvoker())`.

4. Run the full suite to green. Expected: no test failures (assertions don't
   change), dramatic drop in pipeline test times from 30–52 s to the 5–8 s
   range.

### Pitfalls
- `GitSim` creates repos per `GitSim.InitRepo()`. Tests that don't call
  `InitRepo` will see `fatal: not a git repository` for every git probe, which
  matches the existing behavior of `GitInvoker()` on a non-repo temp directory.
  This is correct for tests that don't assert git outcomes.
- Do NOT remove `RealGitIntegrationDriverTests` or any test that exercises real
  git — just gate them behind explicit `new GitInvoker()`.
- Coverage rules apply: no test may be deleted, skipped, or weakened.

## Expected savings

Estimate: pipeline tests drop from 30–52 s → 5–8 s each. Full-suite wall time
should drop from ~92 s to approximately 25–35 s (roughly 35–55 s saving).

## Commit-message evidence

Measure before and after while implementing, then put one filled-in evidence
bullet in the commit message body, following the attached
`commit-message-evidence.md`. Never pre-fill that bullet — numbers are measured
at implementation time and go into the eventual commit message, nowhere else.
