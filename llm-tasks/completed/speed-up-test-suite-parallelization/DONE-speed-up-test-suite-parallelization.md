# Speed Up the Test Suite via xUnit Oversubscription (Config-Only)

The full suite (2755 passed / 22 skipped) takes ~3m42s wall. Measured analysis (2026-07-07,
from `test-logs/*.trx` and a captured full run): restore+build is ~5s warm; everything else is
test execution capped at xUnit's default worker count (= processor count, 12 on the dev Mac),
achieving only ~7.9× effective parallelism because the workload is **wait-dominated** — the
slow tests mostly wait on real child processes, real git repos, and real watchdog timing
windows rather than burning CPU. Thousands of sub-100ms unit tests are ~1% of total time; the
~100 slowest integration-style tests are ~60%.

Because waiting threads don't need cores, oversubscribing the worker pool should cut the wall
time roughly in half (~2 minutes) for a config-only change. A second benefit: ~2 minutes puts
the full suite under the 240-second foreground command ceiling that pipeline stage agents live
with, so in-stage full-suite runs stop timing out and re-running.

## What to change

1. **`tests/VisualRelay.Tests/xunit.runner.json`** (the source file — the copies under `bin/`
   are build outputs). It currently sets only `"parallelizeTestCollections": true`. Add:
   - `"maxParallelThreads": "2.0x"` (multiplier syntax — scales with the machine, so the VM
     with fewer cores gets a proportional pool, not a hardcoded 24), and
   - `"parallelAlgorithm": "aggressive"`.
   The project uses xunit.v3 3.2.2 with xunit.runner.visualstudio 3.1.5 — verify both keys
   against that version's runner-json schema (the file already references
   `https://xunit.net/schema/current/xunit.runner.schema.json`).
2. **Mandatory companion — raise the blame-hang ceiling.** The slowest observed single test is
   ~18.7s and `--blame-hang-timeout` is only 20s in the targeted runner: under oversubscription
   individual tests run slower (more contention), and any test crossing the ceiling is treated
   as hung, killing the whole run. Raise to 120s in every site, consistently:
   - `tools/dotnet-test-files.sh` — both `exec dotnet test …` lines (currently
     `--blame-hang-timeout 20s`);
   - `.relay/config.json` `testCmd` (currently `--blame-hang-timeout 60s`);
   - the advisory mentions in `AGENTS.md` and `TROUBLESHOOTING.md` (currently `30s`) so docs
     match reality.
3. **Do NOT touch** `-m:1` or `-p:UseSharedCompilation=false`: they exist to keep MSBuild/Roslyn
   worker processes from lingering under the sandbox (see the comments in
   `ProcessRunners.SandboxEnv.cs` and `tools/VisualRelay.Guards/RealBuildSubprocessGuard.cs`)
   and cost ~nothing on a warm tree.

## Flake risk and how to handle it

Timing-sensitive tests that are NOT in the serialized `[Collection("Watchdog")]` (e.g.
`ActivityWatchdogSocketWedgeTests`) may flake under a busier scheduler. Protocol: run the full
suite at least 3 consecutive times after the change. If a test flakes, move that test class
into the existing `Watchdog` collection (the established pattern for "tests that launch real
CPU-burning subprocesses" — see the conventions notes in `SplitGuardVerificationTests`) rather
than lowering the thread multiplier. Record any such move and its reason.

## Done when

- Full-suite wall time is measurably reduced (target ≈2 minutes or less on the 12-core dev
  Mac); record before/after wall times from `test-logs/` `.trx` files in the ledger/summary.
- 3 consecutive full-suite runs pass with zero flakes (or flaky classes were moved into the
  Watchdog collection with rationale).
- All `--blame-hang-timeout` sites agree at the new value.
- `./visual-relay check` passes.

## Guardrails

- Config and command-line values only — no test code changes, except the sanctioned
  collection-move for a demonstrated flake.
- Do not reduce test coverage, skip tests, or change test logic to hit the time target.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs.
