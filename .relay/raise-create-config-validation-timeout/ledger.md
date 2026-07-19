## Stage 1 - Ideate

{
  "summary": "The CreateConfigAsync validation timeout is hard-coded at 5 s, which fails on cold builds of SwiftPM, Cargo, Gradle, or dotnet. The fix raises it to 2 min, adds a named constant in ProjectBootstrapper beside the existing timeouts, adds a factory seam for testability, and provides status feedback before the long run. Three options differ in how much abstraction/structure is introduced: Option A (prescribed full seam), Option B (minimal constant-only change), Option C (configuration-driven).",
  "options": [
    "A — Prescribed approach: ProjectBootstrapper constant + factory seam (Func<TimeSpan, ITestRunner>?) + status feedback + 3 VM-level tests via the seam. Matches existing TestCommandFinder pattern.",
    "B — Minimal approach: constant in ProjectBootstrapper, inline value change, status feedback, but no factory seam — test the constant value directly rather than the wiring through the VM.",
    "C — Configuration-driven: load timeout from appsettings/RelayConfig with ProjectBootstrapper constant as fallback. Over-engineered for a fixed value, adds config maintenance burden."
  ]
}

## Stage 2 - Research

{
  "findings": "The codebase is well-structured for this change. ProjectBootstrapper.cs has the two sibling timeout constants (private static readonly, lines 38/45) where a new public constant belongs. MainWindowViewModel.Execution.cs:197 has the target 5-second inline value. A factory-seam pattern already exists via LlmTestCommandFinder (MainWindowViewModel.cs:204) and BackendLifecycleFactory (Commands.cs:50). TestDoubles.cs provides ScriptedTestRunner and TimeoutSimulatingTestRunner that can serve as instant fakes. MainWindowViewModelInitTests.cs in the test project has the existing CreateConfig test. StatusText is an ObservableProperty already available. RelayConfigWriter.Write at Execution.cs:207 writes the config file. The 300-line guard on Execution.cs (currently 299 lines) means the new factory property should go in MainWindowViewModel.cs near TestCommandFinder.",
  "constraints": [
    "Do NOT change DirectExecTestRunner's parameterless-constructor default (5 s) — SandboxedTestRunnerArgumentTests and other call sites depend on it.",
    "Do NOT touch InitValidationTimeout (60 s) or UpgradeValidationTimeout (120 s), and do not merge the new constant with either.",
    "Do NOT change TestCommandValidator.Classify — rejecting on timeout stays correct.",
    "No new test may spawn a real slow process or sleep; tests must use the factory seam with instant fakes.",
    "Place the new factory property in MainWindowViewModel.cs (near TestCommandFinder on line 204), not in Execution.cs (already at 299 lines).",
    "Existing test CreateConfig_WritesConfigAndPopulatesQueue (MainWindowViewModelInitTests.cs:42) must stay green without changes — it spawns real dotnet test.",
    "The new constant must be public static readonly in ProjectBootstrapper, not private like the siblings.",
    "StatusText must be set BEFORE ValidateAsync (not after) to provide feedback during the long run."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The CreateConfig GUI path hard-codes a 5-second validation timeout at MainWindowViewModel.Execution.cs:197 (`new DirectExecTestRunner(TimeSpan.FromSeconds(5))`). This is the outlier versus the sibling init paths in ProjectBootstrapper.cs which use named constants of 60 s (InitValidationTimeout, line 45) and 120 s (UpgradeValidationTimeout, line 38) for the identical smoke-validation pattern through TestCommandValidator.\n\nOn timeout, ProcessCapture.RunAsync (ProcessCapture.cs:173-176) kills the process group and returns the sentinel `(-1, output, TimedOut: true)`. TestCommandValidator.Classify (TestCommandValidator.cs:45-50) unconditionally rejects any TimedOut result with 'test command timed out (timeout, exit code -1)'.\n\nEvidence from patternsmith init (2026-07-18): a cold `swift test` compile took ~2 minutes (`.build` born 19:11:11, products linked ~19:13). The 5 s box killed it mid-compile — validation rejected a perfectly good command. Even a warm re-run (2.6 s wall) had thin margin; a second GUI click still timed out. The perceived ~10 s to error is the 5 s budget plus teardown: SIGINT + up-to-10 s grace before SIGKILL (ProcessCapture.GracefulStop.cs:14) + up-to-4 s output drain (ProcessCapture.cs:16). The budget itself is the root cause at 5 s.\n\nAny toolchain whose test entry point may compile first (SwiftPM, cargo, gradle, dotnet restore) fails this box on first contact, then 'mysteriously' passes once caches warm.\n\nThe test infrastructure already has instant fake runners (ScriptedTestRunner, TimeoutSimulatingTestRunner in TestDoubles.cs:144-168) and an injectable-property pattern (TestCommandFinder at MainWindowViewModel.cs:204). An existing test CreateConfig_WritesConfigAndPopulatesQueue (MainWindowViewModelInitTests.cs:42) spawns real dotnet test and must stay green.\n\nExecution.cs is at exactly 299 lines — right at the 300-line guard — so the new factory property must land in MainWindowViewModel.cs near TestCommandFinder (line 204).",
  "excerpts": [
    "MainWindowViewModel.Execution.cs:197 — `var runner = new DirectExecTestRunner(TimeSpan.FromSeconds(5));` — the hard-coded 5 s timeout that is the root cause.",
    "ProcessCapture.cs:173-176 — `if (timeout != Timeout.InfiniteTimeSpan && await Task.WhenAny(exitedTcs.Task, Task.Delay(timeout, ...)) != exitedTcs.Task) { await GracefulStopThenKillAsync(process, stageGroupId, tp); lock (outputLock) { return (-1, output.ToString(), true); } }` — the timeout mechanism that kills the process group and returns the TimedOut sentinel.",
    "TestCommandValidator.cs:45-50 — `if (runResult.TimedOut) { return ValidationResult.Reject($\"test command timed out (timeout, exit code {runResult.ExitCode})\", runResult); }` — unconditional rejection of any TimedOut result, which is correct behavior but deadly when the budget is only 5 s.",
    "ProjectBootstrapper.cs:38,45 — `private static readonly TimeSpan UpgradeValidationTimeout = TimeSpan.FromSeconds(120);` and `private static readonly TimeSpan InitValidationTimeout = TimeSpan.FromSeconds(60);` — the sibling timeouts demonstrating 5 s is the outlier. A new `CreateConfigValidationTimeout = TimeSpan.FromMinutes(2)` should be added here as `public static readonly`.",
    "MainWindowViewModel.cs:204 — `public LlmTestCommandFinder TestCommandFinder { get; init; } = new();` — the existing injectable-property pattern that the new factory seam should mirror. The new `Func<TimeSpan, ITestRunner>? InitValidationRunnerFactory` property goes here.",
    "TestDoubles.cs:144-168 — `ScriptedTestRunner` (exit-0/green fake) and `TimeoutSimulatingTestRunner` (returns `(-1, \"...\", TimedOut: true)`) — existing instant fakes that the three new VM tests will use via the factory seam.",
    "MainWindowViewModelInitTests.cs:42-56 — `CreateConfig_WritesConfigAndPopulatesQueue` — existing test that spawns real `dotnet test` and must stay green untouched."
  ],
  "repro": "1. On a macOS machine with Xcode/SwiftPM installed, in a new empty directory, create a Package.swift and a single trivial test target. 2. Launch Visual Relay, point it at that directory. 3. Enter `swift test` in the test command input and click 'Create config'. 4. Observe: validation fails within ~10 s (5 s budget + teardown) with 'test command timed out (timeout, exit code -1)' because SwiftPM's cold compile of the package exceeds 5 s. 5. Re-click 'Create config' — it now passes because `.build` is warm and `swift test` returns in ~2 s. The same pattern repros with `cargo test` (no `target/`), `gradle test` (no build cache), or `dotnet test` (no `bin`/`obj`)."
}

## Stage 4 - Plan

{
  "plan": "Raise Create-config validation timeout from 5 s to 2 min with a named constant, factory seam, and status feedback. Five files touched:\n\n1. **ProjectBootstrapper.cs** — add `public static readonly TimeSpan CreateConfigValidationTimeout = TimeSpan.FromMinutes(2)` between `UpgradeValidationTimeout` (line 38) and `InitValidationTimeout` (line 45) with XML doc explaining cold-compile rationale.\n\n2. **MainWindowViewModel.cs** — add `public Func<TimeSpan, ITestRunner>? InitValidationRunnerFactory { get; set; }` after `TestCommandFinder` (line 204). Null default preserves production behavior (falls back to `new DirectExecTestRunner(timeout)`).\n\n3. **MainWindowViewModel.Execution.cs** — in `CreateConfigAsync` (line 192): (a) set `StatusText` to \"Validating test command — a first run may compile and can take up to 2 minutes…\" before the runner, (b) replace inline `TimeSpan.FromSeconds(5)` with factory `InitValidationRunnerFactory?.Invoke(CreateConfigValidationTimeout) ?? new DirectExecTestRunner(CreateConfigValidationTimeout)`. Rejection path untouched.\n\n4. **TestDoubles.cs** — add `StatusCaptureTestRunner(TestRunResult result, Action captureStatus)` after `TimeoutSimulatingTestRunner` (line 168). Captures a snapshot at `RunAsync` time for test 2.\n\n5. **MainWindowViewModelInitTests.cs** — add three [Fact] tests after line 73:\n   - `CreateConfig_UsesCreateConfigValidationTimeout`: factory captures TimeSpan, asserts 2 min, config written.\n   - `CreateConfig_SetsValidatingStatusBeforeValidation`: StatusCaptureTestRunner snapshots StatusText, asserts \"Validating\".\n   - `CreateConfig_RejectsTimeoutAndSurfacesReason`: TimeoutSimulatingTestRunner, asserts status contains \"timed out\", no config written.\n\nAll three new tests use instant fakes (no real processes, no sleeps). Existing `CreateConfig_WritesConfigAndPopulatesQueue` is untouched and stays green.",
  "manifest": [
    "src/VisualRelay.Core/Init/ProjectBootstrapper.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs",
    "tests/VisualRelay.Tests/TestDoubles.cs",
    "tests/VisualRelay.Tests/MainWindowViewModelInitTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 3.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/MainWindowViewModelInitTests.cs",
    "tests/VisualRelay.Tests/TestDoubles.cs"
  ],
  "rationale": "Three new VM tests verify: (1) the factory receives exactly CreateConfigValidationTimeout (2 min), (2) StatusText is set to the validating message before RunAsync, (3) timeout rejection surfaces the reason and prevents config write. The existing CreateConfig_WritesConfigAndPopulatesQueue test remains untouched and green. All tests use instant fakes — no real processes or sleeps."
}

## Stage 6 - Implement

{ "summary": "Raised Create-config validation timeout from 5 s to 2 min via ProjectBootstrapper.CreateConfigValidationTimeout. Added InitValidationRunnerFactory injectable factory seam in Bootstrap.cs (43 lines, under 300-line guard). CreateConfigAsync now sets StatusText before validation and uses the factory with the 2-minute timeout. Three new VM tests (stage 5 authored) verify: (1) factory receives exactly 2 min, (2) StatusText set before RunAsync with 'Validating' message, (3) timeout rejection surfaces reason and prevents config write. Full suite: 3254 passed, 0 failed." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Visual-review

_Skipped: Changes are purely backend/logic: add a TimeSpan constant, injectable factory property, single-line StatusText assignment, and three test cases. No UI markup, layout, styles, images, charts, documents, terminal UI, or any other visual/rendered output is touched._

## Stage 9 - Fix

_Skipped: review passed with no issues._

## Stage 10 - Verify

{
  "summary": "Raised Create-config validation timeout from 5 s to 2 min via a named constant in ProjectBootstrapper, an injectable factory seam (InitValidationRunnerFactory), and a status-feedback message set before the potentially long validation run. Three new VM tests use instant fakes to verify the timeout value, the status message timing, and the timeout-rejection path. Full suite: 3254 passed, 0 failed.",
  "commitMessages": [
    "feat: raise Create-config validation timeout to 2 min",
    "feat(validatation): widen CreateConfig timeout from 5s to 2min with factory seam",
    "feat: add InitValidationRunnerFactory seam and status feedback to CreateConfig",
    "feat: increase CreateConfig smoke-test budget to 2 minutes",
    "feat(ProjectBootstrapper): expose CreateConfigValidationTimeout (2 min)"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

