## Stage 1 - Ideate

{"summary":"Three approaches to a macOS pre-flight gate that waits for an active display before Avalonia initialization: (A) injected-seam loop with CoreVideo P/Invoke probe and caffeinate wake — testable, precise, and preserves crash-on-failure; (B) caffeinate-only pre-flight — simpler but unreliable and not testable; (C) IOKit power-state watcher — more complex with no benefit over the direct CoreVideo check. Option A is recommended as it exactly matches the crash condition and is fully unit-testable.",
"options":["Option A: DisplayReadyGate via CoreVideo P/Invoke + caffeinate + injected-seam retry loop (prescribed approach)","Option B: Single pre-flight caffeinate call without CoreVideo verification","Option C: IOKit-based display power-state poll loop"]}

## Stage 2 - Research

{
  "findings": "The codebase is a .NET 10 (net10.0) Avalonia 12.0.5 desktop app at /Users/nicholaswestby/Dev/visual-relay/. The entry point is src/VisualRelay.App/Program.cs (25 lines), where `Main` calls `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)` unconditionally on line 11-12. There is an existing macOS platform-specific pattern in `MacDockIcon.cs`: `internal static class`, P/Invoke to native libraries, `OperatingSystem.IsMacOS()` guard, best-effort semantics — exactly the pattern to follow for `DisplayReadyGate.cs`. The test project at tests/VisualRelay.Tests/ uses xUnit v3 with parallel collections, and platform-specific tests use `Assert.Skip(...)` with `[Fact]` (no Avalonia dependency needed for DisplayReadyGateTests since the seams are pure C# with no UI). A `TestModuleInitializer` redirects XDG_CONFIG_HOME to a temp dir at assembly load. `SplitGuardVerificationTests.FactCount_AcrossOversizedFiles_MatchesBaseline()` (baseline=175) tracks [Fact] counts across ~30 oversized-file families but would NOT capture a standalone 4-fact test file ≤300 lines (new `DisplayReadyGateTests`). `FileSizeGuard` enforces ≤300 lines per file. No new NuGet dependencies are allowed. `BannedSymbols.txt` forbids `HeadlessUnitTestSession` and `AvaloniaTestFrameworkAttribute`. The project uses `VisualRelay.slnx` solution file. The test project already has a project reference to `src/VisualRelay.App`, so new tests in that project have access to `DisplayReadyGate` (which would be `internal`).",
  "constraints": [
    "dotnet build VisualRelay.slnx must succeed with no errors",
    "All existing tests must pass — no regressions",
    "No behavior change on Windows/Linux — EnsureDisplayReady() must be a guarded no-op via OperatingSystem.IsMacOS()",
    "No new NuGet dependencies allowed in any project",
    "The gate must never convert a hard failure into a silent hang or swallowed error — when all retries exhaust, proceed into Avalonia so the existing loud crash is preserved",
    "Must not use Avalonia APIs before AppMain — gate uses only BCL + P/Invoke",
    "New files must be ≤300 lines (FileSizeGuard enforcement)",
    "DisplayReadyGateTests has ~4 [Fact]s and is a standalone file ≤300 lines, so it does NOT affect the SplitGuardVerificationTests oversized-family baseline (baseline=175)",
    "MacDockIcon.cs provides the exact P/Invoke + best-effort pattern to follow for DisplayReadyGate.cs (internal static class, OperatingSystem.IsMacOS() guard)",
    "Test methods in DisplayReadyGateTests use plain [Fact] (not [AvaloniaFact]) since the injected-seam tests need no Avalonia headless session",
    "Do NOT catch InvalidOperationException from StartWithClassicDesktopLifetime and re-call it — Avalonia setup is not re-entrant",
    "Do NOT wrap launch in caffeinate -d at the launcher/script level",
    "Do NOT switch Avalonia rendering modes or add options to dodge the display link",
    "Do NOT touch the visual-relay bootstrap script"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The crash is deterministic and well-understood. Launching Visual Relay on macOS while the display is asleep causes Avalonia's CVDisplayLink-backed render timer to fail during AppBuilder.Setup() with native error -6661 (kCVReturnInvalidArgument), because CoreVideo's CVDisplayLinkCreateWithActiveCGDisplays requires at least one active display. The process dies with SIGABRT (exit code 134) before any window appears.\n\nEntry point: src/VisualRelay.App/Program.cs, lines 11-12 — Main unconditionally calls BuildAvaloniaApp().StartWithClassicDesktopLifetime(args). There is no guard between process start and Avalonia platform initialization. The file's own comment (lines 7-9) warns not to use Avalonia or SynchronizationContext-reliant APIs before AppMain, which constrains any fix to BCL + P/Invoke only.\n\nExisting pattern to follow: src/VisualRelay.App/MacDockIcon.cs — an internal static class with OperatingSystem.IsMacOS() guard, P/Invoke to native macOS frameworks, best-effort semantics (swallows interop failures), and no new NuGet dependencies. The DisplayReadyGate should follow the same conventions.\n\nWhat needs to be built:\n1. New file src/VisualRelay.App/DisplayReadyGate.cs — an internal static class with:\n   - A testable WaitUntilReady(Func<int> probe, Action wake, Action delay, int maxAttempts) loop that probes readiness (0=success, nonzero=failure), calls wake+delay between failures, returns true on first success or false when maxAttempts exhaust.\n   - Real adapters: probe via P/Invoke CVDisplayLinkCreateWithActiveCGDisplays/CVDisplayLinkRelease from CoreVideo.framework; wake via Process.Start of /usr/bin/caffeinate -u -t 5 (best-effort, don't wait for exit); delay via Thread.Sleep(5000).\n   - Public EnsureDisplayReady() — guarded no-op on non-macOS; on macOS calls WaitUntilReady with maxAttempts=24 (~2 min), logs each retry round to Console.Error, and when the gate gives up logs a final line then proceeds (preserving the existing crash).\n2. Wire into Program.Main: call DisplayReadyGate.EnsureDisplayReady() before BuildAvaloniaApp().\n3. New file tests/VisualRelay.Tests/DisplayReadyGateTests.cs — 4 [Fact] tests exercising WaitUntilReady purely through injected seams (no CoreVideo, no processes): probe-immediately-0, probe-returns-0-after-two-failures, all-attempts-fail, wake-throws-doesnt-abort-loop.\n\nConstraints confirmed:\n- No new NuGet dependencies (DisplayReadyGate uses only BCL + P/Invoke, tests use only xUnit).\n- No behavior change on Windows/Linux (EnsureDisplayReady is guarded no-op).\n- Gate must proceed to Avalonia on exhaustion (preserves existing crash).\n- Test project already has a ProjectReference to src/VisualRelay.App, so internal access is available.\n- DisplayReadyGateTests is ≤300 lines and standalone, so it does NOT affect the SplitGuardVerificationTests.FactCount_AcrossOversizedFiles_MatchesBaseline ratchet (baseline=175).",
  "excerpts": [
    "src/VisualRelay.App/Program.cs:11-12 — Main invokes Avalonia setup unconditionally: `public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);`",
    "src/VisualRelay.App/Program.cs:7-9 — Comment forbids pre-AppMain Avalonia usage: `// Initialization code. Don't use any Avalonia, third-party APIs or any // SynchronizationContext-reliant code before AppMain is called: things aren't initialized // yet and stuff might break.`",
    "src/VisualRelay.App/MacDockIcon.cs — Existing macOS P/Invoke pattern: internal static class, OperatingSystem.IsMacOS() guard, DllImport to native framework, best-effort (swallows failures). Provides the structural template for DisplayReadyGate.cs.",
    "llm-tasks/03-survive-display-sleep-at-launch/03-survive-display-sleep-at-launch.md:7-9 — Crash trace: `Unhandled exception. System.InvalidOperationException: Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661 at Avalonia.Native.AvaloniaNativeRenderTimer.EnsureRegistered()`",
    "llm-tasks/03-survive-display-sleep-at-launch/03-survive-display-sleep-at-launch.md:24 — Scope: `macOS only. Avalonia's macOS render timer is a CoreVideo display link; error -6661 is kCVReturnInvalidArgument, returned when there is no active display to attach to.`",
    "llm-tasks/03-survive-display-sleep-at-launch/03-survive-display-sleep-at-launch.md:37 — Root cause: `Retrying StartWithClassicDesktopLifetime after a failed setup is not safe (Avalonia's locator holds partially-initialized static state), so the failure must be prevented before Avalonia initialization begins, not caught after.`",
    "tests/VisualRelay.Tests/SplitGuardVerificationTests.cs:184 — Fact-count ratchet baseline=175 would NOT include a new standalone DisplayReadyGateTests.cs ≤300 lines.",
    "tests/VisualRelay.Tests/VisualRelay.Tests.csproj:48 — Test project already references src/VisualRelay.App, granting internal access to DisplayReadyGate."
  ],
  "repro": "On a macOS machine:\n1. Force the display to sleep: `pmset displaysleepnow`\n2. Immediately launch Visual Relay: `dotnet run --project src/VisualRelay.App`\n3. Observe: Unhandled exception. System.InvalidOperationException: Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661. Process exits with SIGABRT (exit code 134).\n4. Wake the display (e.g. `caffeinate -u -t 3`) and launch again — succeeds.\n\nThis was deterministically reproduced on 2026-07-12: two launches with display asleep both crashed identically; a launch immediately after waking succeeded."
}

## Stage 4 - Plan

{
  "plan": "### Step 1 — Create `src/VisualRelay.App/DisplayReadyGate.cs`\n\nNew internal static class following the `MacDockIcon.cs` pattern (internal static class, macOS-guarded no-op, P/Invoke, best-effort).\n\n**Public surface:**\n\n- `public static void EnsureDisplayReady()` — the entry point called from `Main`. Guarded no-op when `!OperatingSystem.IsMacOS()`. On macOS, calls `WaitUntilReady` with:\n  - `probe`: a private static method that P/Invokes `CVDisplayLinkCreateWithActiveCGDisplays` / `CVDisplayLinkRelease` from CoreVideo.framework and returns the native result code (`0` = success).\n  - `wake`: a private static method that starts `/usr/bin/caffeinate -u -t 5` via `Process.Start`, best-effort (try/catch, does not wait for exit).\n  - `delay`: `() => Thread.Sleep(TimeSpan.FromSeconds(5))`.\n  - `maxAttempts`: 24 (~2 minutes total budget).\n  - On each retry round, writes to `Console.Error.WriteLine` explaining why startup is waiting. If all attempts fail, writes a final \"giving up\" line and returns (allowing the existing Avalonia crash to fire).\n\n**Testable core:**\n\n```csharp\ninternal static bool WaitUntilReady(\n    Func<int> probe, Action wake, Action delay, int maxAttempts)\n```\n\nLogic:\n1. Call `probe()`. If `== 0`, return `true` immediately.\n2. For `maxAttempts - 1` more rounds: `wake()`, `delay()`, `probe()`. If any probe returns `0`, return `true`.\n3. Return `false` when all probes returned non-zero.\n4. If `wake()` throws, catch and continue (the failure is swallowed; the loop keeps probing).\n\n**P/Invoke declarations** (private, in the class):\n\n```csharp\n[DllImport(\"/System/Library/Frameworks/CoreVideo.framework/CoreVideo\")]\nprivate static extern int CVDisplayLinkCreateWithActiveCGDisplays(out IntPtr displayLink);\n\n[DllImport(\"/System/Library/Frameworks/CoreVideo.framework/CoreVideo\")]\nprivate static extern void CVDisplayLinkRelease(IntPtr displayLink);\n```\n\nThe probe method calls `CVDisplayLinkCreateWithActiveCGDisplays`, releases the link only when the result is `0` (non-zero results mean `out` was not populated, so `IntPtr.Zero` must not be released), and returns the result code.\n\n**Constraints:** No Avalonia APIs. No new NuGet dependencies. ≤300 lines.\n\n---\n\n### Step 2 — Modify `src/VisualRelay.App/Program.cs`\n\nInsert `DisplayReadyGate.EnsureDisplayReady();` before `BuildAvaloniaApp()` in `Main`:\n\n```csharp\n[STAThread]\npublic static void Main(string[] args)\n{\n    DisplayReadyGate.EnsureDisplayReady();\n    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);\n}\n```\n\nFile goes from 25 to 27 lines. The existing comment about not using Avalonia APIs before `AppMain` still holds — the gate uses only BCL + P/Invoke.\n\n---\n\n### Step 3 — Create `tests/VisualRelay.Tests/DisplayReadyGateTests.cs`\n\nFour `[Fact]` tests exercising `WaitUntilReady` purely through injected seams:\n\n1. **`ProbeImmediatelyZero_ReturnsTrueWithoutWake`** — probe returns `0` on first call → returns `true`, `wake` never called, exactly 1 probe.\n2. **`ProbeFailsTwiceThenZero_ReturnsTrueAfterTwoWakes`** — probe returns `-6661`, `-6661`, `0` → returns `true`; `wake` called twice; record call order in a `List<string>` and assert the `probe/wake/delay/probe/wake/delay/probe` sequence.\n3. **`ProbeAlwaysFails_ReturnsFalseAfterMaxAttempts`** — probe always returns `-6661`, `maxAttempts: 3` → returns `false` after exactly 3 probes (and 2 wake/delay cycles).\n4. **`WakeThrows_DoesNotAbortLoop`** — `wake` throws `InvalidOperationException` every time, probe returns `-6661`, `-6661`, `0` → returns `true` (loop survives the thrown exceptions and still discovers the display).\n\nEach test constructs inline lambda seams — no CoreVideo, no processes, no platform dependency. Tests use plain `[Fact]` (not `[AvaloniaFact]`).\n\nFile ≤300 lines. Does not affect the `SplitGuardVerificationTests` baseline (175) — `DisplayReadyGateTests` is not in the oversized-family prefix list.\n\n---\n\n### Step 4 — Build and verify\n\n```bash\ndotnet build VisualRelay.slnx\ndotnet test tests/VisualRelay.Tests\n```\n\nAll existing tests must pass. The file-size guard (`AllTestCsFiles_AreAtMost300Lines`) must pass for the new files.",
  "manifest": [
    "+src/VisualRelay.App/DisplayReadyGate.cs",
    "src/VisualRelay.App/Program.cs",
    "+tests/VisualRelay.Tests/DisplayReadyGateTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 1, untracked deleted: 1.

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/DisplayReadyGateTests.cs"],
  "rationale": "Created DisplayReadyGate.cs (internal static class, 158 lines) with WaitUntilReady(Func<int> probe, Action wake, Action delay, int maxAttempts) — pure injected-seam retry loop that probes a CoreVideo display link, wakes the display via caffeinate on failure, and returns true/false. EnsureDisplayReady() guards on OperatingSystem.IsMacOS(), logs per-retry-round to Console.Error, and proceeds into Avalonia on exhaustion (preserving the existing crash). Wired into Program.Main before BuildAvaloniaApp(). Four [Fact] tests in DisplayReadyGateTests.cs (86 lines) exercise probe-immediately-zero, probe-fails-then-zero (call-order assertion), all-attempts-fail (maxAttempts=3), and wake-throws-survival — all pure C# with no CoreVideo/process dependencies. Build succeeds; all 4 tests pass."
}

## Stage 6 - Implement

{ "summary": "Created DisplayReadyGate.cs (114 lines) with WaitUntilReady retry loop using injected seams, CoreVideo P/Invoke probe, caffeinate wake, and Thread.Sleep delay. Wired EnsureDisplayReady() into Program.Main before Avalonia init. All 4 targeted tests pass; full suite: 3007 passed, 0 failed, 113 skipped." }

## Stage 7 - Review

{ "verdict": "changes", "issues": ["DisplayReadyGate.EnsureDisplayReady is missing the prescribed per-retry Console.Error.WriteLine. The spec requires it to 'write one Console.Error.WriteLine per retry round (so the launcher log shows why startup is waiting) and a final line when giving up.' The current implementation only writes the final give-up line; the probe/wake/delay seams are passed bare to WaitUntilReady with no logging wrapper, so during a multi-minute display-sleep wait the launcher log shows nothing. Wrap the injected seams (or otherwise log) once per retry round in EnsureDisplayReady while keeping WaitUntilReady's pure testable core unchanged."] }

## Stage 8 - Visual-review

_Skipped: No UI, no rendered output — the fix is a pre-Avalonia wait loop (DisplayReadyGate) with injected-seam tests. Nothing in this change affects visuals, layout, styles, or any viewable artifact._

## Stage 9 - Fix

{ "summary": "Fixed the Stage 7 review issue: added per-retry Console.Error.WriteLine logging in DisplayReadyGate.EnsureDisplayReady by wrapping the delay delegate to log \"Display still asleep — retry round N/M...\" before each Thread.Sleep. The WaitUntilReady testable core is unchanged. Targeted tests (4/4) and full suite (3007 passed, 0 failed, 113 skipped) all pass." }

## Stage 10 - Verify

{
  "summary": "Added a macOS-only pre-flight gate (DisplayReadyGate) that waits until a CoreVideo display link can be created before Avalonia initialization, preventing the `InvalidOperationException`/SIGABRT crash that occurs when launching while the display is asleep. The gate probes CVDisplayLinkCreateWithActiveCGDisplays, wakes the display via caffeinate between up to 24 retries (~2 min budget), logs progress to stderr, and falls through to the existing crash on exhaustion. Program.Main calls EnsureDisplayReady() before BuildAvaloniaApp(). No-op off macOS. Four injected-seam unit tests verify the retry loop logic without touching real hardware.",
  "commitMessages": [
    "fix: survive display sleep at app launch on macOS",
    "feat: add pre-flight display-ready gate with CoreVideo probe and caffeinate wake",
    "fix: prevent render-timer crash when display is asleep at startup",
    "feat: gate Avalonia initialization behind active display check on macOS",
    "fix: retry display link creation before starting Avalonia on macOS"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

