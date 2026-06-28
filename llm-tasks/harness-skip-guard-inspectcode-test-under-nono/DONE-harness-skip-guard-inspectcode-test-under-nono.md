# Harness: skip-guard the InspectCode gate-as-test so it stops false-failing under nono

## Problem
`InspectCodeGateZeroFindingsTests.InspectCode_ReportsZeroFindings` is a **gate-as-test**:
it shells out to the external JetBrains/ReSharper **InspectCode** tool over the whole
solution and asserts zero SARIF findings. That child tool needs to write a JetBrains cache
and touch the macOS Security framework — both of which the verify sandbox (`nono` /
`vr-guard`) denies. So the test passes everywhere **except** inside nono.

This is the same class of bug the `harness-verify-survives-sandbox-self-tests` task already
fixed for real-subprocess self-tests (`NonoRealBuildTests` et al.): a VR-on-itself test that
cannot run inside the strict verify sandbox. It slipped back in because the InspectCode
gate-as-test was added **after** that sweep (commit `4b7ac9b`), and the existing
`RealBuildSubprocessGuard` structurally can't see it (the spawn is indirect — see Evidence).

Two paths run the suite, and they disagree:
- `./visual-relay check` runs InspectCode **UNsandboxed** (both as gate-step 6 and again
  inside its unsandboxed `dotnet test`), so the dev gate stays green and nobody noticed.
- VR's per-task **verify** runs the **full suite under nono** via `SandboxedTestRunner`
  ("sandbox always on — there is no opt-out"). There the gate-as-test fails on the denied
  cache write, producing a **FALSE FLAG**: an otherwise-green task is failed by VR's own
  dev-gate test, not by anything the task did.

## Evidence (file:line; denials empirically confirmed by prior tasks)
- **The failing gate-as-test** — `tests/VisualRelay.Tests/InspectCodeGateZeroFindingsTests.cs:29-37`
  (`[Fact]` line 29; method `InspectCode_ReportsZeroFindings` line 30; calls
  `InspectCodeGate.Run(paths)` line 34). Added in commit `4b7ac9b`
  (`fix: clear 77 inspect-code SARIF findings…`) as a fast red→green helper for the
  `harness-clear-inspect-code-debt` clean-up; it has no skip-guard.
- **It really shells out** — `tools/VisualRelay.Cli/Gates/InspectCodeGate.cs:23,29`:
  `ProcessLauncher.Run(ProcessLauncher.Dotnet, ["tool","restore",…])` then
  `ProcessLauncher.Run(ProcessLauncher.Dotnet, ["jb","inspectcode", paths.Solution, … "--caches-home="+cachesHome, …])`.
  The caches-home defaults under `$XDG_CACHE_HOME` / `~/.cache/visual-relay/inspectcode`
  (`InspectCodeGate.cs:67-74`).
- **`./visual-relay check` runs InspectCode UNsandboxed** —
  `tools/VisualRelay.Cli/Commands/CheckCommand.cs:32` (`Gates.InspectCodeGate.Run(paths)`,
  step 6) and the test step at `:35`/`:48` (`ProcessLauncher.Dotnet` direct `dotnet test`,
  no nono wrapping). So in `check`, InspectCode runs twice, both unsandboxed, both green —
  coverage is real there.
- **Per-task verify runs the FULL suite UNDER nono** —
  `src/VisualRelay.Core/Execution/SandboxedTestRunner.cs:12-13` ("The sandbox is always on
  — there is no opt-out") and `:51` (`BuildNonoPrefix(config, rollback:false)` →
  `nono run -p vr-guard --allow-cwd --`). Verify/commit-gate invoke it via
  `_dependencies.TestRunner.RunAsync(rootPath, config.TestCommand, …)`
  (`RelayDriver.CommitGate.cs:42-43`, `RelayDriver.Bootstrap.cs:73`); the production
  `TestRunner` is a `SandboxedTestRunner` (`tools/VisualRelay.RunTask/Program.cs:28`,
  `tools/VisualRelay.DrainQueue/Program.cs:31,43`,
  `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs:80,85,275`). VR's own
  `testCmd` is the whole suite —
  `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false --blame-hang --blame-hang-timeout 60s --blame-hang-dump-type none`
  (see `llm-tasks/harness-revert-split-verify/harness-revert-split-verify.md:15`) — so
  `InspectCode_ReportsZeroFindings` IS in the sandboxed run.
- **Why the sandbox denies it** — `packaging/nono/vr-guard.json`:
  - `"write": []` is empty (lines 47-48): no host write is granted; only the workspace is
    writable, via `--allow-cwd`. The `allow` list (lines 9-46) grants specific toolchain
    caches (NuGet, `.dotnet`, swiftpm, npm, cargo, go, pip, uv, …) but **no JetBrains/
    ReSharper cache** and **not** `~/.cache/visual-relay`. So InspectCode's cache write —
    both its `--caches-home` and the JetBrains global-tool cache under
    `~/Library/.../JetBrains/` — is **denied**. This is the deterministic failure.
  - `unsafe_macos_seatbelt_rules` (lines 94-102) grant no `com.apple.SecurityServer`
    mach-lookup, so the **keychain / Security-framework** lookup the JetBrains tool attempts
    is also **denied**. (Caveat from `llm-tasks/completed/harness-verify-reason-ignore-nono-advisory`:
    nono prints a `mach-lookup (com.apple.SecurityServer) … Keychain access requires
    granting the login keychain path` advisory on *every* sandboxed failure, so treat the
    cache-write denial as the root cause and the keychain line as the known advisory — do
    not chase it.)
- **The opt-in skip pattern to reuse** —
  `tests/VisualRelay.Tests/NonoRealBuildTests.cs:90-100`, the `SkipIfNotOptedIn()` helper:
  it reads `Environment.GetEnvironmentVariable("VR_RUN_NONO_INTEGRATION")` and, unless it
  equals `"1"`, calls `Assert.Skip("VR_RUN_NONO_INTEGRATION=1 required …")` (lines 92-97);
  it also `Assert.Skip`s when `nono` is not on PATH (lines 98-99). Each `[Fact]` calls it
  first (e.g. lines 18, 37, 53, 71). A second precedent uses the inline form —
  `tests/VisualRelay.Tests/PackagingToolTests.cs:163-166`
  (`Environment.GetEnvironmentVariable("VR_RUN_NONO_INTEGRATION") … Assert.Skip("…spawns a real dotnet run.")`).
  Result: these tests **skip in the default sandboxed suite** and only run when explicitly
  opted in.
- **The subprocess guard exists but does NOT cover this test** —
  `tools/VisualRelay.Guards/RealBuildSubprocessGuard.cs` + the live gate
  `RealBuildSubprocessGuardTests.AllTestProjectCsFiles_AreSandboxBuildSafe`
  (`tests/VisualRelay.Tests/RealBuildSubprocessGuardTests.cs:262-279`). It flags only a
  **literal** `new ProcessStartInfo(...)` / `Process.Start(...)` whose program is a known
  build tool and whose first arg is a heavy verb (`RealBuildSubprocessGuard.cs:44-58,124-161`),
  reading program/verb from **string literals only** (`:257-263`), scanning only
  `tests/VisualRelay.Tests`. `InspectCodeGateZeroFindingsTests.cs` contains **no**
  `ProcessStartInfo`/`Process.Start` — it calls `InspectCodeGate.Run(paths)`, and the real
  spawn is `new ProcessStartInfo(fileName)` with a **variable** program in
  `tools/VisualRelay.Cli/ProcessLauncher.cs:26` (outside the scanned tree, with a verb of
  `tool`/`jb`, not a HeavyVerb). So the guard **structurally cannot** see this indirect
  gate-as-test. That gap is exactly why this regressed past the guard.

## What to do
Skip-guard the InspectCode gate-as-test under nono using the **same opt-in mechanism** as
the other sandbox-incompatible tests — do **not** loosen the sandbox.
- In `InspectCodeGateZeroFindingsTests.InspectCode_ReportsZeroFindings`, gate on
  `VR_RUN_NONO_INTEGRATION=1` exactly like `NonoRealBuildTests.SkipIfNotOptedIn()` (reuse
  that helper's idiom, or call an equivalent env-checked `Assert.Skip(...)` at the top of
  the test). It then **skips in the normal sandboxed suite** and **still runs on demand**
  when opted in.
- **Coverage is not lost.** InspectCode still runs (unsandboxed, green) in
  `./visual-relay check` — both as gate-step 6 (`CheckCommand.cs:32`) and inside that path's
  `dotnet test`. The skip only removes the *redundant, sandbox-incompatible* in-suite copy
  from the per-task verify run. Update the test's XML-doc to say so, so a future reader
  doesn't "restore" the unguarded gate.
- **Recurrence prevention (bring it under the guard, or document why not).** The
  `RealBuildSubprocessGuard` can't catch an indirect gate-as-test (see Evidence). Prefer a
  **minimal extension** so the class can't recur: have the guard (or a small sibling
  guard-as-test) also flag a test method that invokes a known unsandbox-only **gate entry
  point** — `InspectCodeGate.Run` (and, defensively, other `…Gate.Run(paths)` shell-out
  gates) — unless it carries the `VR_RUN_NONO_INTEGRATION` opt-in skip. Keep it
  literal/AST-based and self-exempt like the existing guard. If that is judged out of scope,
  **state explicitly in the task** why not (indirect, non-literal spawn) and that the
  skip-guard alone is the accepted mitigation — do not silently leave the gap.
- **General-purpose note.** This is test-infra over **VR's own dev gate** (the InspectCode
  zero-findings check), not the relay engine that runs on user codebases, so VR-specific
  symbols here are appropriate. It must **not** touch engine behavior and must **not** weaken
  the sandbox or the nono profile — the *test* is skipped in the sandboxed run, the sandbox
  is unchanged.

## Alternative(s)
- **Auto-skip when running under the sandbox** (no env flag): detect nono at runtime (e.g. a
  sandbox-injected env marker, or probe that a known-denied write fails) and `Assert.Skip`
  then. This needs no opt-in to exercise the test locally, but it adds a bespoke detection
  path and diverges from the house idiom. **Prefer the established `VR_RUN_NONO_INTEGRATION`
  opt-in** for consistency with `NonoRealBuildTests` / `PackagingToolTests` and so the
  recurrence-guard can key on the one well-known marker.
- **Grant the JetBrains cache / keychain in `vr-guard.json`:** rejected — explicitly
  forbidden by `harness-verify-survives-sandbox-self-tests` ("Do NOT weaken the sandbox").

## Done when
- The **full suite runs GREEN under nono** (`testCmd` in a verify worktree) with **no**
  InspectCode false-failure — `InspectCode_ReportsZeroFindings` is skipped in the default
  sandboxed run. Validate by exit code + stored verify output, not by parsing pass/fail
  counts, and with **zero** sandbox relaxation.
- `./visual-relay check` is still green and **still runs InspectCode** unsandboxed (gate-step
  6 stays authoritative for the dev gate).
- The test **still runs on demand**: `VR_RUN_NONO_INTEGRATION=1 dotnet test --filter …`
  executes `InspectCode_ReportsZeroFindings` (skipped without the flag).
- If applicable, the subprocess/gate-as-test guard now **covers** this test (a new
  un-skip-guarded shell-out gate-as-test fails the build); otherwise the task records why it
  doesn't and that the skip-guard is the accepted mitigation.
- Conventional Commit (e.g. `test(verify): skip-guard the InspectCode gate-as-test under nono`).

## Priority
High — this is a standing **false-flag**: it fails otherwise-green tasks at verify for a
reason intrinsic to VR's own dev gate, exactly the failure mode
`harness-verify-survives-sandbox-self-tests` was meant to end.
