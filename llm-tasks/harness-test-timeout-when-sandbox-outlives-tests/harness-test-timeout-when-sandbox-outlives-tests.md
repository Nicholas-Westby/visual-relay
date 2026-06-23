# Stop the sandboxed test run from timing out after the tests have finished

The stage-5 red gate (and the stage-9 baseline verify — same path) reports
`test command timed out after 600000ms` even when the test suite **ran to completion in a
fraction of the cap**. The recent harness fix (`0c6f184 fix(harness): end test run on process
exit, not on stream EOF`) and the headless-listener fix (`35b4042 fix(tests): disable
vr-control listener in the headless test app`) were both aimed at exactly this hang and did
**not** resolve it, because they operate one layer below where the stall actually is.

> Note: this is the harness path that *processes tasks*, so a fix may need to be verified
> outside the normal pipeline (or apply the contributing-factor narrowing first) — otherwise
> running this very task through stage 5 trips the same timeout.

## Evidence — run `run-all-button-disabled-after-creating-tasks`, stage 5

From `.relay/run-all-button-disabled-after-creating-tasks/NEEDS-REVIEW` and `run.log`:

- **22:47:12** — red gate spawns
  `nono run … -- /bin/sh -c 'dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false'`;
  the 600 000 ms cap starts.
- **~22:49:31** — the inner `dotnet test` finishes:
  `Failed: 1, Passed: 1857, Skipped: 11, Total: 1869, Duration: 2 m 19 s` → `Command exited with code 1`
  (the *expected* red result — the authored test fails without the implementation).
- **22:57:12** — exactly 600 s after spawn, the run is flagged `test command timed out after 600000ms`.

So the tests finished at ~2m19s, the runner then sat idle for another ~7.5 min, and only then
declared a 10-minute timeout. The fixed binary was live (`VisualRelay.Core.dll` built 15:40:38,
app started 15:40:44; `0c6f184` committed 15:36; the run began 15:44) — this is a **post-fix**
failure, not a stale binary.

## Root cause

`SandboxedTestRunner.RunAsync` (`src/VisualRelay.Core/Execution/SandboxedTestRunner.cs:26`)
runs the test command as `nono run --profile … --allow-cwd -- …`
(`src/VisualRelay.Core/Execution/ProcessRunners.cs:128` `BuildNonoPrefix`) through
`ProcessCapture.RunAsync` with the 600 s cap (`testTimeoutMs`).

After `0c6f184`, `ProcessCapture.RunAsync` ends the run on the **directly-spawned process's**
`Exited` event (`src/VisualRelay.Core/Execution/ProcessCapture.cs:131-137,168`). On the
sandboxed path the directly-spawned process is **`nono`**, not `dotnet`. `nono` supervises the
sandboxed process tree and does **not** exit until orphaned `dotnet test` descendants drain —
testhost / MSBuild node-reuse workers / the build server (`-p:UseSharedCompilation=false` only
disables the Roslyn VBCSCompiler server, not these). Those orphans outlive the 600 s cap, so
`nono`'s `Exited` never fires in time and the cap fires.

That is why `0c6f184` didn't help: it changed the wait from "stream EOF" to "direct-child
exit", but on this path **the direct child (`nono`) is itself the lingering process**, not a
leaked descendant of an already-exited spawn. Both conditions are gated on the same un-exiting
`nono`, so nothing improved. Its regression test
`ProcessCapture_ReturnsPromptlyWhenChildInheritsPipeAndSurvives`
(`tests/VisualRelay.Tests/FdLeakTests.cs`) bakes in the opposite assumption (parent exits
immediately, a grandchild lingers). The sibling fix `35b4042` removed one orphan source (the
headless `ControlServer` `HttpListener`) but not the dotnet-test orphans, so `nono` still
lingered.

This is a **harness-wide** problem, not specific to VR's own suite: Visual Relay runs against
arbitrary target repos and cannot assume it can stop every target's test command from leaving
orphans. The harness has to defend itself.

(Diagnostic aid for confirming the specific orphan: `./visual-relay test --blame-hang
--blame-hang-timeout 30s`, and `ps` for surviving `nono`/`dotnet`/`testhost`/`MSBuild`
during/after a gate run — see `TROUBLESHOOTING.md`.)

## Contributing factor — the red gate always runs the whole suite

`.relay/config.json` sets `testFileCmd` **identical** to `testCmd`, with **no `{files}`
placeholder**:

```jsonc
"testCmd":     "dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false",
"testFileCmd": "dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false",
```

So the stage-5 red gate's `config.TestFileCommand.Replace("{files}", …)`
(`src/VisualRelay.Core/Execution/RelayDriver.Stage5.cs:98`) is a **silent no-op** and runs all
**1869** tests (2m19s) instead of the one authored test. This makes every red gate slow **and
maximizes the window in which `dotnet test` leaves lingering descendants** — directly feeding
the timeout above. The agent's own in-stage run *did* narrow correctly
(`--filter FullyQualifiedName~CreateNewTask_EnablesDrainQueueCommand`, 85 ms), proving a
targeted form exists and the gate just isn't using one. A `{files}`-less `testFileCmd` silently
degrading to a full-suite run is itself a latent trap for any project.

## What to build

Do all three.

1. **Harness-side resilience (primary — works for any target repo).** Give the sandboxed
   test-run path the completion/idle detection the *agent* path already has. Today
   `SandboxedTestRunner` calls `ProcessCapture.RunAsync` with **no** `onActivity`, `killToken`,
   `cpuSampleIntervalMs`, or watchdog (`SandboxedTestRunner.cs:26`) — the only guard is the hard
   cap. Wire in an `ActivityWatchdog` + CPU-tree sampling the way `SwivalSubagentRunner.RunAsync`
   does (`src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs:88-114`) so that once the
   process tree goes **output-silent and CPU-idle** for a grace window, the runner reaps the
   wrapper and returns the **completed** result instead of riding the cap.
   - *Design point (open):* report the real outcome, not a timeout. The inner exit status is
     present in nono's captured output (`Command exited with code N`) and/or becomes available
     once the source-side fix (2) lets `nono` exit with the inner code. Decide how to surface
     red-vs-green when the wrapper is reaped on idle vs. when it exits cleanly. A genuine hang
     (never completes; tree busy, or silent from the very start) must still cap out — distinguish
     "finished then idle" from "never produced output".

2. **Source-side leak reduction (defense in depth for VR's own suite).** Stop `dotnet test`
   from leaving orphans so `nono` can exit on its own — which also makes `0c6f184` work as
   intended and yields nono's true exit code. Levers: set `MSBUILDDISABLENODEREUSE=1` (and
   `DOTNET_CLI_TELEMETRY_OPTOUT=1`) in the sandbox env
   (`SwivalSubagentRunner.BuildSandboxEnvironment`, used at `SandboxedTestRunner.cs:25`), and/or
   run `dotnet build-server shutdown` after the gate, and/or ensure testhost terminates. Confirm
   no orphaned `nono`/`dotnet`/`testhost`/MSBuild processes survive a red-gate run.

3. **Fix the contributing factor.** Give this project a genuinely narrowing `testFileCmd` (a
   `--filter`/`{files}` form like the agent already used) so the red gate runs only the authored
   tests. AND make the harness defensive: when a configured `testFileCmd` contains no `{files}`
   token — so the gate silently runs the whole suite — warn at config load / surface it, because
   that is a footgun for every target project.

## Done when

- A red-gate run whose inner test command finishes well under the cap **returns promptly**
  (seconds after the tests finish), reporting the real red/green outcome — it no longer waits
  out the full `testTimeoutMs`. Verified by re-running a representative task (e.g.
  `run-all-button-disabled-after-creating-tasks`) and seeing stage 5 complete on the test's
  actual duration, not the cap.
- A genuinely hung test command (no completion) still caps out and is reported as a halt with
  the existing actionable `ErrorHintClassifier` hint.
- No orphaned `nono` / `dotnet` / `testhost` / MSBuild processes survive a completed red-gate
  run — or, if any do, the harness no longer blocks on them.
- The stage-5 red gate runs a **narrowed** test command for this project (only the authored
  test files), and a `{files}`-less `testFileCmd` no longer silently degrades to a whole-suite
  run without warning.
- The stage-9 baseline verify (same `SandboxedTestRunner` path) gets the same resilience.
- A regression test covers the **inverted** scenario that `0c6f184` missed: a sandbox-like
  wrapper process that **stays alive after its child exits**, asserting the runner returns the
  child's result promptly instead of timing out at the cap (the inverse of
  `ProcessCapture_ReturnsPromptlyWhenChildInheritsPipeAndSurvives`).
