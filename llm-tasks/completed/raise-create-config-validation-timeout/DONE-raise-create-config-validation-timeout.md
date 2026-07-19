# Raise the Create-config test validation timeout from 5 s to 2 minutes

The GUI "Create config" path smoke-validates the entered test command with a
hard-coded 5-second budget: `CreateConfigAsync` constructs
`new DirectExecTestRunner(TimeSpan.FromSeconds(5))`
(src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs:197). On
timeout, ProcessCapture kills the process group and returns the `(-1, output,
TimedOut: true)` sentinel (src/VisualRelay.Core/Execution/ProcessCapture.cs:173-176),
which `TestCommandValidator.Classify` rejects unconditionally as
"test command timed out (timeout, exit code -1)"
(src/VisualRelay.Core/Init/TestCommandValidator.cs:45).

Five seconds is the outlier: the sibling init paths in
`ProjectBootstrapper` already budget 60 s (`InitValidationTimeout`,
ProjectBootstrapper.cs:45) and 120 s (`UpgradeValidationTimeout`,
ProjectBootstrapper.cs:38) for the identical smoke-validation.

## Evidence (2026-07-18, patternsmith init)

- First `Create config` click against /Volumes/Tera/dev/patternsmith ran
  `./test.sh` (`swift test`) with NO existing `.build`: the cold compile took
  ~2 minutes (`.build` born 19:11:11, products linked ~19:13). The 5 s box
  killed it mid-compile — validation rejected a perfectly good command.
- Warm re-run in a bare shell: 2.6 s wall — under the box, but thin margin;
  a second GUI click still timed out. Perceived ~10 s to the error is the 5 s
  budget plus teardown: SIGINT, up-to-10 s grace before SIGKILL
  (ProcessCapture.GracefulStop.cs:14), up-to-4 s output drain
  (ProcessCapture.cs:16). The budget itself is still 5 s.
- Any toolchain whose test entry point may compile first (SwiftPM, cargo,
  gradle, `dotnet restore`) fails this box on first contact, then
  "mysteriously" passes once caches warm.

## Prescribed approach

Give the Create-config path a 2-minute budget, named and documented next to
the other validation timeouts, and make the wiring testable without real
slow processes.

### Steps

1. `ProjectBootstrapper` (src/VisualRelay.Core/Init/ProjectBootstrapper.cs):
   add `public static readonly TimeSpan CreateConfigValidationTimeout =
   TimeSpan.FromMinutes(2);` beside the existing two timeouts. XML-doc the
   why: a first-ever run may cold-compile the whole package (measured ~2 min
   for a small SwiftPM package); value matches `UpgradeValidationTimeout`
   today but is a deliberately separate knob for the manual GUI path.
2. `MainWindowViewModel.Execution.cs` `CreateConfigAsync`: replace the inline
   `TimeSpan.FromSeconds(5)` with the new constant.
3. Testability seam, mirroring the existing injectable `TestCommandFinder`
   property: add a settable VM property
   `Func<TimeSpan, ITestRunner>? InitValidationRunnerFactory` (name to
   taste), defaulting to `t => new DirectExecTestRunner(t)` when null.
   `CreateConfigAsync` obtains its runner through it, passing
   `CreateConfigValidationTimeout`. Production behavior is unchanged.
4. Status feedback: immediately before `ValidateAsync`, set `StatusText` to
   something like "Validating test command — a first run may compile and can
   take up to 2 minutes…". A 2-minute silent button reads as a hang; that is
   exactly how this bug got reported.
5. Tests (tests/VisualRelay.Tests, red first, no real sleeps — the
   RealSleepGuard and the test-speed work stay respected; all new tests use
   the factory seam with an instant fake):
   - VM test: inject a factory that records the `TimeSpan` it receives and
     returns a fake runner yielding exit 0. Execute `CreateConfigCommand`;
     assert the factory was called with exactly 2 minutes and the config was
     written. (Red today: no seam, and the value is 5 s.)
   - VM test: fake runner's `RunAsync` captures `StatusText` at call time;
     assert it contains the validating message (proves the status is set
     BEFORE the potentially long run, not after).
   - VM test: fake runner returns `TimedOut: true, ExitCode: -1`; assert
     `StatusText` surfaces the existing "timed out" rejection and no
     `.relay/config.json` is written — the reject path is preserved.
6. Existing `MainWindowViewModelInitTests.CreateConfig_WritesConfigAndPopulatesQueue`
   (real `dotnet test` spawn) must stay green untouched.

### Guardrails

- Do NOT change `DirectExecTestRunner`'s parameterless-constructor default
  (5 s) — other call sites rely on it (e.g. SandboxedTestRunnerArgumentTests).
- Do NOT touch `InitValidationTimeout` (60 s) or `UpgradeValidationTimeout`
  (120 s), and do not merge the new constant with either — same value,
  different knob.
- Do NOT change `TestCommandValidator.Classify` — rejecting on timeout stays
  correct; this task only widens the budget and improves feedback.
- No new test may spawn a real slow process or sleep to prove the timeout;
  the factory seam exists so the value is asserted directly.
- Mind the 300-line file guard when adding the VM property; place it in
  whichever partial holds `TestCommandFinder` if Execution.cs is tight.

## Done when

Full suite green via `./visual-relay check`; the three new VM tests pass;
clicking Create config on a never-built repo (cold `.build` equivalent)
validates instead of dying at 5 s, with the validating status visible during
the run.
