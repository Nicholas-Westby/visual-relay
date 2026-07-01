# Stabilize the Verify Gate (Whack-a-Mole Test Fixes)

VR's full test suite intermittently fails on unrelated tests — a *different* one on each fix-verify
attempt — so a correct task can exhaust its fix-verify budget and flag without its own change ever being
at fault. This task fixes the flaky and latent tests behind that. Do all three fixes together; for each,
fix **both** the concrete failure **and** the thing that let it happen.

## Background: the observed whack-a-mole

A recent task flagged after 3 fix-verify attempts. Its own feature was fine; the gate tripped on a
different unrelated test each round:

| Round | What actually failed | Related to that task? |
|---|---|---|
| Verify | file-size guard ×2 + a scroll-layout test | file-size = self-inflicted, scroll = no |
| Fix-verify 1 | scroll-layout test | no |
| Fix-verify 2 | `ChevronAffordanceRenderTests` type-init crash → **whole run aborted** | no |
| Fix-verify 3 | `ControlServer_Dispose_ReleasesListener` — "Address already in use" | no |

Three of these are genuine bugs in **VR's own test suite** and are fixed below (Chevron, ControlServer,
scroll). The fourth — the file-size guard failing only at the full gate — is **not** a VR test bug and is
out of scope here; do **not** "fix" it by baking a file-size rule into the pipeline (that rule is a
VR-repo convention, and VR runs on arbitrary codebases).

Fix the three test bugs below.

---

## Part A — `ChevronIcon` type-initializer crash (HIGHEST impact: aborts the whole run)

**Symptom.** In `tests/VisualRelay.Tests/ChevronAffordanceRenderTests.cs`, two test methods —
`SharedGeometry_IsOpticallycenteredInIconBox` and `ChevronForeground_HasExplicitDefault_NotNull`
— are plain `[Fact]`s. Both touch `ChevronIcon` (e.g. `ChevronIcon.SharedGeometry`, `new ChevronIcon()`),
whose static initializer is `SharedGeometry = Geometry.Parse("M 4 2.5 L 8 6 L 4 9.5")`. That
`Geometry.Parse` requires `Avalonia.Platform.IPlatformRenderInterface`, which only exists once the
headless Avalonia platform is initialized.

**Why it aborts the gate.** A plain `[Fact]` does **not** initialize the Avalonia platform (only
`[AvaloniaFact]`/`[AvaloniaTheory]` do). When such a `[Fact]` happens to run before any `[AvaloniaFact]`,
`ChevronIcon`'s static initializer throws `TypeInitializationException` and **permanently poisons the
type for the whole test process**, so the next `[AvaloniaFact]` touching `ChevronIcon` crashes/hangs
→ `--blame-hang` → "Test Run Aborted". This is non-deterministic because it depends on test execution
order, and it takes down the entire suite, not one test.

**Fix the failure.** Convert both `[Fact]` methods to `[AvaloniaFact]` (the class's other five tests
already use `[AvaloniaFact]`, and the class already carries `[Collection("Headless")]`). Do **not**
change any assertion.

**Fix the enabler.** The reflection guard that is *supposed* to catch this class of problem does not
go far enough. Today `SplitGuardVerificationTests` (see `tests/VisualRelay.Tests/SplitGuardVerificationTests.Headless.cs`,
method `HeadlessTestClasses_AllCarryHeadlessCollectionAttribute`) only enforces the *forward*
direction — "any class with `[AvaloniaFact]`/`[AvaloniaTheory]` must carry `[Collection("Headless")]`".
It never catches a plain `[Fact]`/`[Theory]` living **inside** a Headless class, which is exactly the
poison hazard.

Add a new guard rule (a new `[Fact]` in `SplitGuardVerificationTests`, or an extension of the existing
one): **no plain `[Fact]`/`[Theory]` test methods are permitted in a class carrying
`[Collection("Headless")]`** — every test method in a Headless class must be `[AvaloniaFact]`/
`[AvaloniaTheory]`. Rationale: tests in a Headless class share the process-global Avalonia platform;
a plain `[Fact]` that runs first can poison platform-dependent type initializers for the rest of the
process. The failure message must list each offending `Class.Method`.

**Important:** once the new rule exists it will likely surface **more** violations than just the two
Chevron tests. Fix *every* violation it reports — convert to `[AvaloniaFact]`/`[AvaloniaTheory]`, or
move a genuinely platform-free test out of the Headless class. Never delete or `Skip` a test to make
the guard pass.

---

## Part B — `ControlServer.Dispose` does not release the port deterministically

**Symptom.** `ControlServerEndToEndTests.ControlServer_Dispose_ReleasesListener`
(`tests/VisualRelay.Tests/ControlServerTests.cs`) intermittently fails with
`System.Net.HttpListenerException : Address already in use`. It disposes a `ControlServer` and then
*immediately* binds a fresh `HttpListener` on the same port to assert the port was freed.

**Root cause.** In `src/VisualRelay.App/Services/ControlServer.cs`, `Start()` launches the accept loop
with `Task.Run(() => AcceptLoopAsync(...))` and does not keep a handle to that task. `Stop()` (called by
`Dispose()`) cancels the CTS and calls `_listener.Stop()`/`_listener.Close()` but **never awaits the
accept loop**. So when `Dispose()` returns, the background `GetContextAsync` unwind and socket teardown
may still be in flight, and an immediate rebind of the same port races and loses. `Dispose()`'s
contract ("releases the port") is effectively asynchronous.

**Fix the failure + enabler.** Make disposal release the port deterministically:
- Capture the accept-loop `Task` returned by `Task.Run(...)` in `Start()` (store it in a field).
- In `Stop()`, after cancelling and calling `Stop()`/`Close()`, **await that task to completion with a
  bounded timeout** so the listener is fully torn down before `Stop()` returns. Preserve the existing
  contract that teardown never throws (keep it best-effort / swallow inside the bounded wait).

**Also make the test resilient to OS-level timing.** Even with a correct `Dispose`, kernel socket
teardown (e.g. `TIME_WAIT`) can vary across platforms. After `server.Dispose()`, poll `probe.Start()`
on the same prefix with a short **bounded retry** (e.g. retry for up to ~1–2 s) instead of assuming the
port is instantly bindable. Keep the assertion's intent intact (the port *does* become bindable) — do
not weaken it into a no-op.

---

## Part C — Headless UI tests read the developer's real `ui-state.json`

**Symptom.** `TaskDetailScrollBottomReachabilityTests.MarkdownReadOnly_Extent_ReachesTextBlockBottom_WithGap`
(`tests/VisualRelay.Tests/TaskDetailScrollBottomReachabilityTests.cs`) flakes depending on the machine
it runs on. Its `LoadPanelAsync` helper builds a real `MainWindowViewModel` and calls `LoadInitialAsync()`.

**Root cause.** `LoadInitialAsync` loads layout via `UiStateStore.Load()`
(`src/VisualRelay.Core/Configuration/UiStateStore.cs`). Called with the default `accessor: null`,
`UiStateStore.Load` resolves `$XDG_CONFIG_HOME/visual-relay/ui-state.json` (falling back to
`~/.config/visual-relay/ui-state.json`) — i.e. the **real developer machine's** persisted
`ActivityColumnWidth`. On the flagged run this was `573`, which starved the center column so the
markdown `TextBlock` never overflowed its viewport, breaking the test's "content must overflow"
precondition. The tests are not hermetic.

**Fix the failure + enabler.** Make MainWindow UI-state loading hermetic under test — fix it at the
*source*, not with a per-test band-aid (the flagged run's stop-gap of setting
`IsActivityColumnCollapsed = true` in the test helper was discarded and is the wrong layer). Preferred
approach: thread an `IEnvironmentAccessor` (or an explicit UI-state source) into `MainWindowViewModel`
so `UiStateStore.Load(accessor)` reads from an **isolated** location under test; have the headless test
harness (e.g. `TestRepository` / the shared headless fixture) provide an isolated, empty XDG config dir
so the load falls back to `UiState` defaults (`ActivityColumnWidth = 340`) regardless of the machine.

This is the same family as the existing effort to isolate profile/config writes in driver tests — align
with that pattern rather than inventing a new one. While here, audit the other headless tests that build
a real `MainWindow`/`MainWindowViewModel` and bring them under the same isolated state so no UI test can
be flaked by machine-persisted layout.

---

## Global constraints

- **All three fixes in one run.** The `Verify` gate must end green: `Failed: 0`, no crash, no abort, no
  blame-hang dump, exit code 0.
- **Never weaken the gate to pass it.** No deleting tests, no `Skip = "..."`, no loosened assertions.
  VR's own reflection guards forbid this and it defeats the purpose of the task.
- **Keep every new/edited `*.cs`/`*.axaml` file ≤ 300 lines** (VR's own file-size guard enforces this).
- **The new AvaloniaFact/Headless guard must pass**, which means fixing *all* violations it surfaces,
  not only the two Chevron tests.
- After the change, the new guard rule should **fail on the pre-fix tree** (proving it catches the
  Chevron `[Fact]` case) and **pass after**. The `ControlServer` and scroll tests should be deterministic
  across repeated runs.

## Files likely in scope (the plan stage will finalize the manifest)

- `tests/VisualRelay.Tests/ChevronAffordanceRenderTests.cs` — `[Fact]` → `[AvaloniaFact]`
- `tests/VisualRelay.Tests/SplitGuardVerificationTests.Headless.cs` — new guard rule
- `src/VisualRelay.App/Services/ControlServer.cs` — await accept loop on `Stop()`
- `tests/VisualRelay.Tests/ControlServerTests.cs` — bounded-retry rebind
- `src/VisualRelay.App/ViewModels/MainWindowViewModel*.cs` — injectable/isolated UI-state
- the headless test harness (`TestRepository` / shared fixture) — isolated XDG config dir
- possibly additional test files surfaced by the new Headless guard
