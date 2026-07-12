# Survive a sleeping display at app launch

## Problem

Launching Visual Relay while the Mac's display is asleep crashes the process before the window ever exists:

```
Unhandled exception. System.InvalidOperationException: Avalonia.Native was not able to start the RenderTimer. Native error code is: -6661
   at Avalonia.Native.AvaloniaNativeRenderTimer.EnsureRegistered()
   ...
   at Avalonia.AppBuilder.Setup()
   at Avalonia.AppBuilder.SetupWithLifetime(IApplicationLifetime lifetime)
   at Avalonia.ClassicDesktopStyleApplicationLifetimeExtensions.StartWithClassicDesktopLifetime(...)
   at VisualRelay.App.Program.Main(String[] args)
```

The process exits with SIGABRT (exit code 134). This was reproduced deterministically on 2026-07-12: two launches with the display asleep both crashed identically; a launch immediately after waking the display succeeded.

This matters because Visual Relay is routinely launched unattended — the self-development relaunch loop rebuilds and restarts the app via the control API, often while nobody is at the machine and the display has gone to sleep. Every such relaunch dies until a human wakes the screen.

Scope of the defect, established experimentally:

- **Launch-time only.** A running instance survives display sleep indefinitely: with the display forced off (`pmset displaysleepnow`), an already-running app kept its process alive and its control API answering `/health` with 200 continuously, and prior instances have run across overnight display sleeps for days.
- **macOS only.** Avalonia's macOS render timer is a CoreVideo display link; error `-6661` is `kCVReturnInvalidArgument`, returned when there is no active display to attach to. Windows/Linux backends do not have this failure mode.
- **Waking the display fixes it.** `caffeinate -u -t 3` (declares user activity, which lights the display; ships with macOS) followed by a launch succeeds; the display then re-sleeps on its normal power-management schedule.

## Root cause

`Main` in `src/VisualRelay.App/Program.cs` runs Avalonia setup unconditionally:

```csharp
[STAThread]
public static void Main(string[] args) => BuildAvaloniaApp()
    .StartWithClassicDesktopLifetime(args);
```

`AppBuilder.Setup()` creates the CVDisplayLink-backed render timer during platform initialization, and there is no guard for the display being asleep. Retrying `StartWithClassicDesktopLifetime` after a failed setup is not safe (Avalonia's locator holds partially-initialized static state), so the failure must be prevented *before* Avalonia initialization begins, not caught after.

## Fix

Add a macOS-only pre-flight gate in `Main`, before `BuildAvaloniaApp()`, that waits until a display link can be created — waking the display to make that happen promptly.

### 1. Testable wait loop (new file `src/VisualRelay.App/DisplayReadyGate.cs`)

A small static class whose loop logic takes injected seams so tests never touch real hardware:

```csharp
internal static class DisplayReadyGate
{
    /// <summary>
    /// Returns true when the display became ready within the attempt budget
    /// (or was ready immediately); false when the budget was exhausted.
    /// probe() returns a CoreVideo result code (0 = success);
    /// wake() requests a display wake; delay() sleeps between attempts.
    /// </summary>
    internal static bool WaitUntilReady(
        Func<int> probe, Action wake, Action delay, int maxAttempts)
}
```

Behavior: if the first `probe()` returns 0, return true without calling `wake`. Otherwise loop: `wake()`, `delay()`, `probe()` — up to `maxAttempts` total probes; return false when all fail. No timers or clocks inside the loop; pacing lives entirely in the injected `delay`.

### 2. Real adapters (same file)

- Probe: P/Invoke CoreVideo directly —

  ```csharp
  [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
  private static extern int CVDisplayLinkCreateWithActiveCGDisplays(out IntPtr displayLink);

  [DllImport("/System/Library/Frameworks/CoreVideo.framework/CoreVideo")]
  private static extern void CVDisplayLinkRelease(IntPtr displayLink);
  ```

  The probe calls create, releases the link when the result is 0 (non-zero results return `IntPtr.Zero` and must not be released), and returns the result code. This is the same call Avalonia's render timer depends on, so probe success is the exact readiness condition.
- Wake: start `/usr/bin/caffeinate` with arguments `-u -t 5` via `Process.Start`, best-effort (swallow launch failures, do not wait for exit).
- Delay: `Thread.Sleep(TimeSpan.FromSeconds(5))`.
- Entry point used by `Main`: a `public static void EnsureDisplayReady()` that is a no-op unless `OperatingSystem.IsMacOS()`, calls `WaitUntilReady(probe, wake, delay, maxAttempts: 24)`, writes one `Console.Error.WriteLine` per retry round (so the launcher log shows why startup is waiting) and a final line when giving up.

### 3. Wire into `Main` (`src/VisualRelay.App/Program.cs`)

```csharp
[STAThread]
public static void Main(string[] args)
{
    DisplayReadyGate.EnsureDisplayReady();
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}
```

When the gate gives up (headless Mac with no display at all, or graphics stack genuinely broken), proceed into Avalonia anyway so the existing loud crash is preserved — the gate must never convert a hard failure into a silent hang or a swallowed error. Keep the "don't use Avalonia APIs before AppMain" constraint from the file's comment: the gate uses only BCL + P/Invoke.

## Rejected approaches — do not do these

- Do NOT catch the `InvalidOperationException` from `StartWithClassicDesktopLifetime` and re-call it — Avalonia setup is not re-entrant after a failed initialization.
- Do NOT wrap the launch in `caffeinate -d` (prevent display sleep) at the launcher/script level — that keeps the user's monitors on around the clock, which is the operational problem that motivated this task.
- Do NOT switch Avalonia rendering modes or add Avalonia options to dodge the display link; the platform default rendering path stays as-is.
- Do NOT touch the `visual-relay` bootstrap script.

## Tests

New file `tests/VisualRelay.Tests/DisplayReadyGateTests.cs`, exercising `WaitUntilReady` purely through the injected seams (no CoreVideo, no processes, platform-independent):

- probe immediately 0 → returns true, `wake` never called, exactly one probe.
- probe returns -6661 twice then 0 → returns true, `wake` called twice, delays interleaved (record call order in a list and assert the `probe/wake/delay` sequence).
- probe always -6661 with `maxAttempts: 3` → returns false after exactly 3 probes.
- `wake` throwing must not abort the loop (the failure is swallowed and the loop continues probing).

`EnsureDisplayReady` itself is a thin adapter and is exercised implicitly on macOS dev machines at every app start; do not write a test that P/Invokes CoreVideo or spawns caffeinate.

## Constraints

- `dotnet build VisualRelay.slnx` must succeed; all existing tests must pass.
- No behavior change on Windows/Linux (`EnsureDisplayReady` is a guarded no-op).
- No new NuGet dependencies.
- If a fact-count ratchet test guards a touched test class, bump the ratchet to match — never remove tests to satisfy it.
