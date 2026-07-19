# Task: Add a serial test mode — `./visual-relay test serial [Filter]`

The parallel suite's per-test timings are unusable for finding slow tests. Add
an opt-in serial mode that runs test collections one at a time so a timing run
produces trustworthy per-test numbers. Default behavior must not change.

### Evidence (2026-07-19 slow-test investigation)

- `tests/VisualRelay.Tests/xunit.runner.json` sets `parallelizeTestCollections:
  true`, `maxParallelThreads: "2.0x"`, `parallelAlgorithm: "aggressive"`. In a
  full-suite TRX the per-test durations sum to ~5,200s inside ~92s of wall
  clock (host run `test-logs/20260712T222826_Mac_17600.trx`: 3,174 tests, sum
  5,204s, wall 91s; VM run `20260718T124658_…_5259.trx`: 3,306 tests, sum
  5,194s, wall 92s). A test's reported duration is dominated by time queued
  between awaits, not work.
- Concrete: `RelayDriverVerifyFixTests.RunTaskAsync_VerifyGreen_…` reported
  29s in a full host run but measures 0.016s in a filtered class run;
  `ControlServerKestrelHandlerTests.UnknownRoute_Returns404_Json` swung 0.0s ↔
  20s between two full runs. See attached `investigation-baseline.md`.
- `tools/VisualRelay.Cli/TestRunner.cs:38-47` — a leading non-flag token
  becomes the `FullyQualifiedName~` filter; remaining args are forwarded to
  `dotnet test` verbatim.
- xunit accepts RunSettings overrides as inline `dotnet test` arguments after
  `--`, e.g. `dotnet test -- xUnit.ParallelizeTestCollections=false`
  (https://xunit.net/docs/runsettings; the repo uses xunit.v3 +
  xunit.runner.visualstudio 3.1.5, which honor the `xUnit.*` keys).
- `tools/VisualRelay.Cli/WatchdogTimeouts.cs:12-13` — the test watchdog
  defaults to 60s (`VISUAL_RELAY_TEST_TIMEOUT` overrides). A serial full suite
  will exceed 60s by construction (the parallel run already takes ~92s).

### What to build

Exactly this shape, in `tools/VisualRelay.Cli/TestRunner.cs`:

1. Extract the argument construction into a pure, testable
   `internal static` builder (e.g. `BuildTestArgs(...)`) that `RunAsync` calls.
   Keep behavior byte-identical for existing inputs.
2. Recognize the literal first token `serial` (ordinal comparison): consume
   it and set serial mode. After consuming it, the existing rule applies
   unchanged to the remaining tokens — a next non-flag token becomes the
   filter, the rest are forwarded. `./visual-relay test serial`,
   `./visual-relay test serial GitCommitter`, and `./visual-relay test
   GitCommitter` must all work.
3. In serial mode append the RunSettings tail as the final arguments:
   `--` then `xUnit.ParallelizeTestCollections=false`. Tests within a
   collection already run sequentially, so this one key yields a fully serial
   run. Do not modify `xunit.runner.json` (the default run keeps its
   parallelism).
4. In serial mode, when `VISUAL_RELAY_TEST_TIMEOUT` is unset, use a 1800s
   watchdog default instead of 60s (add a serial-aware resolver next to
   `WatchdogTimeouts.ForTest()`; the env var still wins when set).
5. Print one stderr line when serial mode is active, alongside the existing
   log-path lines, e.g. `serial mode: one collection at a time; per-test
   timings are trustworthy`.
6. Update the CLI usage/help text where `test` is documented (grep the CLI
   project for the existing `test` usage string) and add a short note to the
   test-runner section of `TROUBLESHOOTING.md` saying serial mode exists and
   what it is for.

Out of scope: combining `serial` with a caller-supplied `--` RunSettings tail;
if the forwarded args already contain `--`, fail with a clear error rather
than merging.

### Constraints

- Default (non-serial) invocations must produce byte-identical `dotnet test`
  arguments to today — prove it with the builder tests.
- No new dependencies; keep files under the 300-line guard.
- Coverage is non-negotiable: no test is deleted, skipped, or weakened.

### Tests (red first)

In a new `CliSerialTestModeTests` (pattern: `CliTestLogPathsTests`):

- `serial` leading token → args end with `--` followed by
  `xUnit.ParallelizeTestCollections=false`, and no
  `FullyQualifiedName~serial` filter is present.
- `serial GitCommitter` → both the RunSettings tail and
  `--filter FullyQualifiedName~GitCommitter` are present.
- `GitCommitter` alone → args identical to today's (no RunSettings tail).
- Serial + `VISUAL_RELAY_TEST_TIMEOUT` unset → 1800s; set to `90` → 90s
  (exercise the pure `Resolve`-style seam, not process-global env state).

### Verification

- `./visual-relay check` fully green.
- Manual: `./visual-relay test serial CliSerialTestModeTests` runs and the
  stderr note appears; `NO_BUILD=1 ./visual-relay test serial` completes a
  full serial suite without tripping the watchdog.

### Commit-message evidence

Measure before and after while implementing (a serial run's wall time vs the
parallel run's is the natural pair), then put one filled-in evidence bullet in
the commit message body, following the attached `commit-message-evidence.md`.
Never pre-fill that bullet — numbers are measured at implementation time and
go into the eventual commit message, nowhere else.
