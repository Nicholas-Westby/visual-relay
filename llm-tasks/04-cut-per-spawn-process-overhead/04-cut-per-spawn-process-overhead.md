# Task: Cut fixed overhead paid on every spawned process

Two per-spawn costs are paid thousands of times per suite run (and in
production): every `new GitInvoker()` re-probes the git binary with a real
`git --version` subprocess, and every process that exits through
`ProcessCapture` triggers a full OS process-table walk via
`Kill(entireProcessTree: true)` — even a single short-lived `git` call with no
descendants. Cache the probe process-wide and make the tree-reap opt-out for
single-process invocations.

### Evidence (2026-07-19 slow-test investigation)

- `src/VisualRelay.Core/Execution/GitInvoker.cs:115` — the constructor path
  calls `ResolveGitBinary()` (`:132`), which spawns a real probe with
  `--version` (`:250`) per INSTANCE. 28 test files construct their own
  invoker; the queue controller constructs one per drain
  (`RelayQueueController.cs:162`) and one per flagged-task reset
  (`RelayQueueController.PrivateHelpers.cs:40`).
- `src/VisualRelay.Core/Execution/ProcessCapture.cs:180-186` — on the NORMAL
  exit path: `KillProcessGroup` (when a stage group id exists) and
  `process.Kill(entireProcessTree: true)`. The comment explains the purpose:
  reap surviving descendants so inherited pipe write-ends close and the
  bounded drain EOFs fast. On macOS, enumerating descendants walks the
  process table (sysctl) at ~5-40ms per call, worse under parallel-suite
  load. For a lone `git` invocation there are no persistent descendants (git
  waits for its own children before exiting), so the walk is pure overhead.
- Effect size measured 2026-07-19: an effective git call in this suite costs
  ~30-120ms against ~20-80ms for a bare spawn; real-git test families make
  8-33 calls per test. The reap cost also lands on every one of the
  thousands of subprocess exits the suite produces.
- Guard context: `FdLeakTests` (e.g.
  `ProcessCapture_DetachedChildReapedAfterNormalExit`,
  `ProcessTreeCpuSampler_DrainedOnAllPaths`) exist specifically to protect
  the reap/drain behavior — they must keep covering the default path.

### What to build

Two focused commits:

1. **Probe cache.** Move git binary resolution behind a process-wide
   `private static readonly Lazy<string>` (thread-safe publication) so the
   first `GitInvoker` pays the probe and every later instance reuses the
   resolved path. Resolution logic itself is unchanged. Expose an
   `internal static int ProbeCount` (incremented inside the Lazy factory)
   solely so the test below can assert the cache works; no public surface
   change.
2. **Opt-out tree reap.** Add a `bool reapProcessTree = true` option to the
   `ProcessCapture` run entry point (threaded to the normal-exit block at
   `ProcessCapture.cs:180-186`). When false, the normal-exit path skips both
   `KillProcessGroup` and `Kill(entireProcessTree: true)` and goes straight
   to the bounded drain. Timeout and cancellation paths ALWAYS reap,
   regardless of the flag. `GitInvoker` passes `reapProcessTree: false`;
   every other caller keeps the default.

### Constraints

- Stage/agent process handling is untouched: default `reapProcessTree: true`
  everywhere except `GitInvoker`; kill-on-timeout and kill-on-cancel behavior
  identical.
- Coverage is non-negotiable: no test deleted, skipped, or weakened.
  `FdLeakTests` keep exercising the default-reap path unchanged.
- Keep files under the 300-line guard; do not add new public API beyond the
  optional parameter.

### Tests (red first)

- `GitInvoker` probe cache: construct two invokers; assert the probe ran at
  most once (via the internal counter; account for other tests having warmed
  the Lazy by asserting the count does not increase between the two
  constructions).
- `ProcessCapture` plumbing: run a trivial fast command (e.g. `/usr/bin/true`)
  with `reapProcessTree: false` → exit code 0, output captured, returns
  promptly. Run the same with the default → identical result (behavioral
  parity smoke; pattern: `ProcessCaptureEnvStripTests`).
- Reap-still-works: the existing `FdLeakTests` remain green with no edits
  (they are the regression net for the default path).

### Verification

- `./visual-relay check` fully green.

### Commit-message evidence

Measure before and after while implementing (a real-git-heavy class like
`RewriteHistoryRunnerTests` solo is a good scope for the probe/reap effect),
then put one filled-in evidence bullet in the commit message body, following
the attached `commit-message-evidence.md`. Never pre-fill that bullet —
numbers are measured at implementation time and go into the eventual commit
message, nowhere else.
