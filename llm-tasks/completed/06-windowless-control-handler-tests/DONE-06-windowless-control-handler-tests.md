# Task: Make the control-API handler tests windowless and leave the headless UI collection

`ControlServerKestrelHandlerTests` are in-memory handler tests ("no sockets,
no ports") yet every request helper inflates a full `MainWindow` on the single
shared headless dispatcher — the serial lane all ~380 `[AvaloniaFact]` tests
queue through. The handler tests don't render anything: the window exists only
because `ControlApi`'s constructor demands one (it serves the `/screenshot`
route). Give `ControlApi` a windowless mode, drop these tests to plain
`[Fact]`, and shrink the serial floor every UI test shares.

### Evidence (2026-07-19 slow-test investigation)

- `tests/VisualRelay.Tests/ControlServerKestrelHandlerTests.cs:17-33` — the
  class is `[Collection("Headless")]`; `InvokeAsync` builds
  `new MainWindowViewModel(...)`, `new MainWindow { DataContext = vm }`, and
  `new ControlApi(vm, window)` PER CALL, then exercises
  `ControlServer.BuildHandler` against a `DefaultHttpContext`. The class doc
  itself says "no sockets, no ports".
- The "Headless" collection is one serial xunit collection sharing one
  dispatcher thread across ~60 files / ~380 tests (`HeadlessTestApp.cs`,
  `HeadlessCollectionDefinition.cs`) — every needless `MainWindow` XAML
  inflate lengthens the queue for all of them.
- Measured: `UnknownRoute_Returns404_Json` reported 20s in the 2026-07-18/19
  host run, 0.0s in the 2026-07-12 host run (pure queue variance), and 0.25s
  in a solo class run (first-test app/XAML init; siblings 0.01-0.02s).
- Repo convention, AGENTS.md:35-43: UI tests instantiate the minimal slice,
  and `SplitGuardVerificationTests.WholeAppBoot.cs:130`
  (`NoTestFile_BootsWholeAppOutsideAllowlist`) enforces an allowlist for
  whole-app boots. Plain-`[Fact]` precedent for VM construction off the
  dispatcher: `MainWindowViewModelTests`.

### What to build

1. **Windowless `ControlApi`.** Make the window collaborator optional at the
   `ControlApi` seam (nullable parameter or `Func<MainWindow?>` — pick
   whichever matches its current field usage; verify the window is consumed
   only by the screenshot path before proceeding, and if any other route
   touches it, route that access through the same null-guard). When absent,
   `/screenshot` returns `503` with a one-line JSON body naming the reason
   (`window unavailable`). Production wiring in `App.axaml.cs` continues to
   pass the real window, so shipped behavior is unchanged.
2. **Windowless tests.** `ControlServerKestrelHandlerTests.InvokeAsync` stops
   constructing `MainWindow` and passes the windowless form; the class drops
   `[Collection("Headless")]` and its facts become plain `[Fact]`
   (`MainWindowViewModel` construction does not need the dispatcher).
3. **Allowlist shrink.** Remove the class from the whole-app allowlist in
   `SplitGuardVerificationTests.WholeAppBoot.cs` so the guard forbids it from
   regressing to a `MainWindow` boot.
4. **Screenshot coverage mapping.** The screenshot route keeps real-window
   coverage wherever it lives today (check `ControlServerKestrelTests` /
   screenshot-related tests); add the new 503 unit test for the windowless
   branch. Record the name-by-name mapping in the run summary.
5. **Bounded audit.** List (run summary only, no fixes beyond clear cases) any
   other `[Collection("Headless")]` class that constructs `MainWindow` yet
   never renders or asserts on visuals — candidates for a follow-up task.

### Constraints

- Production `/screenshot` behavior unchanged; only the no-window branch is
  new, and only tests exercise it.
- Coverage is non-negotiable: no test deleted, skipped, or weakened; every
  reshaped test keeps its name or is mapped name-by-name.
- Keep files under the 300-line guard.

### Tests (red first)

- New unit test: handler built with no window → `GET /screenshot` returns 503
  with the documented body; every other existing route test passes unchanged
  through the windowless helper.
- Guard: `NoTestFile_BootsWholeAppOutsideAllowlist` stays green AFTER the
  allowlist entry is removed (proving the class no longer boots the app).

### Verification

- `./visual-relay check` fully green.
- Manual: `./visual-relay launch` then `curl -s
  http://127.0.0.1:8765/screenshot -o /tmp/vr.png` still returns a PNG.

### Commit-message evidence

Measure before and after while implementing (solo class time, plus the
Headless-collection total if measurable), then put one filled-in evidence
bullet in the commit message body, following the attached
`commit-message-evidence.md`. Never pre-fill that bullet — numbers are
measured at implementation time and go into the eventual commit message,
nowhere else.
