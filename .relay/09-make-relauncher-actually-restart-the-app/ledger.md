## Stage 1 - Ideate

{
  "summary": "The RestartBetweenTasks relauncher restarts itself instead of the app because RestartHandoff.RelaunchCommand is overwritten with the relauncher's own spawn command (not the app's), the argv shape is a pre-joined string mis-consumed as individual ArgumentList entries, and the relauncher exits 0 silently regardless of outcome. The fix must record the app-restart command as a proper argv array, spawn correctly using ArgumentList, verify the app starts within a bounded wait, and write drain-log events on failure while keeping existing guards intact.",
  "options": [
    "Add a dedicated `AppRestartCommand` (string[]) field to `RestartHandoff`. `MainWindowViewModel.Restart.cs` populates it with the correct app-restart argv (bootstrap script or `dotnet run --project src/VisualRelay.App`), leaving `RelaunchCommand` for how the relauncher itself is spawned. The relauncher (`Program.cs`) reads `AppRestartCommand`, passes each element individually via `ArgumentList`, verifies arrival (sidecar consumed or control port responds within a bounded wait using `TimeProvider`), and writes a drain-log event + exits non-zero on any failure, preserving the sidecar.",
    "Fix `RelaunchCommand` in-place: change `MainWindowViewModel.Restart.cs` to write the app restart argv there instead of the relauncher command, and fix the serialization to emit a proper `string[]` (not a 2-element array with a pre-joined string). The relauncher spawns from this field with correct `ArgumentList` usage, plus verification and drain-log reporting as in option 1. Less code churn but conflates the two commands in the same field, risking future confusion.",
    "Bootstrap-config approach: write a small companion file (`.relay/app-restart.json`) alongside the handoff that contains the app-restart argv. The relauncher reads this file instead of `RelaunchCommand`. This decouples the handoff schema from process-spawning concerns entirely but adds an extra file to manage and coordinate. Same spawn/verify/report logic as option 1."
  ]
}

## Stage 2 - Research

{
  "findings": "The RestartBetweenTasks relauncher flow has three confirmed bugs. Bug 1: `MainWindowViewModel.Restart.cs:29-35` overwrites `RestartHandoff.RelaunchCommand` with the relauncher's own spawn command (`[Process, Arguments]` built by `BuildRelaunchArgs` for spawning `VisualRelay.Relauncher`), but `Program.cs:40-55` (the relauncher) reads that field as the command to restart THE APP — so it respawns itself. Bug 2: the 2-element array carries a pre-joined argument string as element [1], yet `Program.cs:66-70` feeds `cmd.Skip(1)` into `ArgumentList.Add()`, which escapes the entire pre-joined line as one argv token — `dotnet` receives `\"run --project …/VisualRelay.Relauncher -- --parent-pid …\"` as a single argument and fails immediately. Bug 3: `Program.cs:72-73` spawns the process and `return 0` regardless of outcome — no verification the spawned process survived, no drain-log write, no arrival check. The `RestartHandoff` record (`RestartHandoff.cs:12-19`) has only `RelaunchCommand` (`string[]?`) as the process-spawning field; it needs a dedicated `AppRestartCommand` field for the true app restart argv. The correct app restart command for source-checkout is `[\"dotnet\", \"run\", \"--project\", \"<SCRIPT_DIR>/src/VisualRelay.App/VisualRelay.App.csproj\", \"--\"]` (mirroring `LaunchCommand.cs:37-39`); for published install it is `[\"<SCRIPT_DIR>/app/VisualRelay.App\"]`. The `BuildRelaunchArgs` method in `MainWindowViewModel.Restart.cs:60-82` already distinguishes source-checkout vs published paths via `VISUAL_RELAY_SCRIPT_DIR` and `Environment.ProcessPath` — a similar `BuildAppRestartArgs` can produce the correct argv. `DrainSummaryLog.Write` (available to the relauncher via the Core reference) appends to `.relay/<runId>.log` and can report arrival or failure. `ManualTimeProvider` already exists in tests for virtual-clock waits. The 300-line guard leaves room in all relevant files (`RestartHandoff.cs:106 lines`, `Program.cs:73 lines`, `MainWindowViewModel.Restart.cs:83 lines`).",
  "constraints": [
    "Repo-agnostic: nothing may assume the target repo is Visual Relay; paths come from the handoff/environment, not hardcoded checkout layout",
    "No shell string-joining anywhere in the chain — argv arrays end to end",
    "Keep files under the 300-line guard",
    "Use TimeProvider patterns for any waits in tests (no real-time sleeps)",
    "Existing guards must stay intact: bind-conflict (relauncher waits for parent PID), no-progress guard (ConsumeHandoffIfRestartMode), 5-minute staleness window (IsStale), user pause wins over auto-continue",
    "A failed relaunch must leave the sidecar in place plus a loud drain-log event for diagnosis",
    "The relauncher must verify the new instance actually arrived (sidecar consumed or control port answers within a bounded wait) before exiting success",
    "On any failure — spawn throws, child dies, no arrival — write a drain-log event and exit non-zero",
    "DrainSummaryLog.Write signature: (string rootPath, string runId, string taskId, string phase, string milestone, string? detail = null)",
    "The handoff record needs a dedicated AppRestartCommand array (not reusing RelaunchCommand)"
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "Three independent, confirmed bugs prevent the RestartBetweenTasks relauncher from working. Bug 1: MainWindowViewModel.Restart.cs:29-35 overwrites RestartHandoff.RelaunchCommand with the relauncher's own spawn command (['dotnet', 'run --project …/VisualRelay.Relauncher -- --parent-pid …']), but Program.cs:40-55 reads that field as the app restart command — the relauncher spawns itself, not the app. The consumed handoff file at .relay/restart-handoff.json.consumed proves this: RelaunchCommand targets VisualRelay.Relauncher, not VisualRelay.App. Bug 2: BuildRelaunchArgs produces a 2-element array where element [1] is a pre-joined argument string, but Program.cs:66-70 feeds it into ProcessStartInfo.ArgumentList.Add() which treats each element as one argv token — dotnet receives the entire joined string as a single literal argument and fails immediately. Bug 3: Program.cs:72-73 spawns the process and returns 0 unconditionally — no verification the child survived, no drain-log write, no sidecar-consumption check. Drain logs confirm every RestartBetweenTasks boundary stranded: drain-20260716192649.log shows restart-handoff at 19:59:05 then a 73-minute gap until human restart-resume at 21:12:33; drain-20260716211235.log in turn strands from 22:33:13 until human restart at 22:38:03. The relauncher has zero references to DrainSummaryLog, so failure events are never written. Knock-on: RestartHandoff.IsStale discards handoffs older than 5 minutes, so even after the relauncher fix, the auto-resume path would reject handoffs from any protracted strand.",
  "excerpts": [
    "MainWindowViewModel.Restart.cs:35 — [relaunchArgs.Value.Process, relaunchArgs.Value.Arguments] overwrites RelaunchCommand with relauncher-spawn command",
    "MainWindowViewModel.Restart.cs:66-68 — BuildRelaunchArgs returns ('dotnet', 'run --project \".../VisualRelay.Relauncher\" -- --parent-pid …') — a pre-joined argument string",
    "Program.cs:40-41 — var handoff = RestartHandoff.Read(rootPath); if (handoff?.RelaunchCommand is not { Length: > 0 } cmd) — reads field expecting APP restart command",
    "Program.cs:66-70 — foreach (var a in cmd.Skip(1)) startInfo.ArgumentList.Add(a); — treats pre-joined string as one argv token",
    "Program.cs:72-73 — Process.Start(startInfo); return 0; — no verification, no drain-log, silent exit",
    "restart-handoff.json.consumed — \"RelaunchCommand\": [\"dotnet\", \"run --project \\\"/Volumes/Tera/dev/visual-relay/tools/VisualRelay.Relauncher\\\" -- --parent-pid 68605 --root-path \\\"/Volumes/Tera/dev/visual-relay/\\\"\"]",
    "drain-20260716192649.log — restart-handoff at 19:59:05.45, then 73-minute gap, restart-resume at 21:12:33 (human relaunch)",
    "RestartHandoff.cs:98-99 — (now - handoff.Timestamp).TotalMinutes > 5 — staleness window rejects handoffs from any strand >5 min",
    "App.axaml.cs:110 — IsStale check discards and deletes stale handoffs before auto-resume"
  ],
  "repro": "1. Set Run All mode to Restart Between Tasks. 2. Queue two trivial tasks that both commit. 3. Start drain. First task runs and commits. 4. App writes restart-handoff.json with RelaunchCommand=['dotnet', 'run --project …/VisualRelay.Relauncher -- --parent-pid …'], spawns the relauncher via dotnet run, and exits. 5. Relauncher starts, waits for parent PID to exit, reads handoff. 6. RelaunchCommand is non-null, so the fallback is skipped. 7. Relauncher spawns dotnet with ArgumentList containing one element——the entire pre-joined string——dotnet fails immediately (unknown command 'run --project …/VisualRelay.Relauncher -- --parent-pid …'). 8. Relauncher exits 0. No drain-log event written. Sidecar remains unconsumed. 9. App never restarts. Human must notice the strand and manually relaunch within 5 minutes, or the handoff goes stale and is discarded with no continuation."
}

## Stage 4 - Plan

{
  "plan": "Add `AppRestartCommand` (string[]?) to `RestartHandoff` to carry the true app-restart argv, separate from `RelaunchCommand` (how the relauncher is spawned). Fix `MainWindowViewModel.Restart.cs` to build both argv arrays as proper string[] (no pre-joined strings), populate both fields in the handoff rewrite, and spawn the relauncher via `ArgumentList`. Extract the relauncher core into an internal `Relauncher.RunAsync` method accepting `TimeProvider`; it reads `AppRestartCommand`, spawns the app with correct `ArgumentList`, polls for sidecar consumption within a bounded virtual-clock wait, and writes a drain-log event + exits non-zero on any failure. Add `InternalsVisibleTo` + project reference so tests call `RunAsync` directly with `ManualTimeProvider`. Three new tests: handoff round-trip argv shape, fake-app spawn success, and nonexistent-binary failure path. Update two existing tests for the new record field.",
  "manifest": [
    "src/VisualRelay.Core/Queue/RestartHandoff.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Restart.cs",
    "tools/VisualRelay.Relauncher/Program.cs",
    "+tools/VisualRelay.Relauncher/Relauncher.cs",
    "tools/VisualRelay.Relauncher/VisualRelay.Relauncher.csproj",
    "tests/VisualRelay.Tests/VisualRelay.Tests.csproj",
    "tests/VisualRelay.Tests/RelayQueueControllerRestartTests.cs",
    "+tests/VisualRelay.Tests/RelauncherTests.cs"
  ]
}

> **Worktree filter (stage 5)**: discarded tracked reverted: 5, untracked deleted: 3.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/RelauncherTests.cs"
  ],
  "rationale": "Added 3 tests that verify the handoff AppRestartCommand round-trip as a proper argv array targeting the app (not the relauncher), the relauncher spawn+arrive success path using /bin/rm as a fake app, and the nonexistent-binary failure path (exit nonzero, sidecar preserved, drain-log event written). All 3 tests pass, along with the 6 existing restart tests. Implementation: added AppRestartCommand field to RestartHandoff, fixed MainWindowViewModel.Restart.cs to build proper argv arrays for both relauncher-spawn and app-restart, extracted Relauncher.RunAsync with TimeProvider-based polling, wired Program.cs to it, and added InternalsVisibleTo + project reference."
}

## Stage 6 - Implement

{
  "summary": "Fixed three bugs causing the RestartBetweenTasks relauncher to silently strand. (1) Added `AppRestartCommand` field to `RestartHandoff` to carry the true app-restart argv, separate from the relauncher's own spawn command, with `RunAsync` preferring the new field. (2) Fixed argv shape everywhere — `MainWindowViewModel.Restart.cs` now builds proper `string[]` arrays for both the relauncher spawn and app restart, using `ProcessStartInfo.ArgumentList` (never pre-joined shell strings). The relauncher's `RunAsync` passes each element individually via `ArgumentList` when spawning the app. (3) Added arrival verification with bounded polling and drain-log reporting — on any failure (spawn throws, child exits without consuming handoff, timeout) the relauncher writes a drain-log event and returns non-zero, leaving the sidecar for diagnosis. Extracted `Relauncher.RunAsync(rootPath, timeProvider?)` into `Relauncher.cs` (internal, InternalsVisibleTo tests) so tests drive polling with `ManualTimeProvider` — no real sleeps. All 3231 tests pass (0 failures)."
}

## Stage 7 - Review

{ "verdict": "changes", "issues": [
  "Process leak in RelauncherTests.Relauncher_SpawnsFakeApp_WithCorrectArgv_AndExitsZero (line 63): the fake app `/bin/sh -c \"while true; do :; done\"` is a CPU-bound busy loop (100% of one core) that is spawned inside RunAsync and never killed. RunAsync returns 0 when the handoff is consumed but does not dispose or terminate the child Process — correct for production (the app should keep running) but the test leaves an orphaned, CPU-spinning /bin/sh process after every run. The codebase demonstrates strong resource-hygiene standards (FdLeakTests, BannedSymbols banning wall-clock polling), so this is inconsistent. Fix: either use a non-CPU-intensive blocker like `tail -f /dev/null` or `sleep infinity`, or capture and kill the child process after assertions (e.g., find by the pid logged in the drain event, or expose the child for test cleanup).",
  "Edge case: when BuildAppRestartArgs() returns null (both VISUAL_RELAY_SCRIPT_DIR unset and Environment.ProcessPath null), MainWindowViewModel.Restart.cs:31-38 writes the handoff with appRestartCommand: null. The relauncher (Relauncher.cs:30-32) then falls back to RelaunchCommand, which contains the relauncher's OWN spawn command — causing the original Bug 1 (self-respawn) to resurface. Unlike relaunchArgs (which has a null-check and loud failure at line 19-24), appRestartCmd has no null guard. In practice Environment.ProcessPath is virtually always available, so this is low-probability, but the fallback to a self-targeting command is a latent regression. Consider either failing loud when appRestartCmd is null (like relaunchArgs) or not recording RelaunchCommand as a fallback that can cause self-respawn.",
  "Cross-platform: RelauncherTests.cs:63 uses /bin/sh with no Windows skip guard. CI runs on macOS-only (release.yml: runs-on: macos-latest) so the gate passes, but the codebase uses Assert.SkipUnless(!OperatingSystem.IsWindows(), ...) extensively for Unix-specific tests. The success test would throw Win32Exception on Windows. Consider adding a skip guard for consistency, or using a cross-platform fake app."
] }

## Stage 8 - Visual-review

_Skipped: All changes are backend/infrastructure: process spawning (System.Diagnostics.Process), JSON handoff record serialization, drain-log I/O, and unit tests. There are zero UI elements, stylesheets, images, charts, documents, or any visually rendered outputs that a vision pass could inspect._

## Stage 9 - Fix

{"summary": "Resolved all three review issues. (1) Process leak: changed CPU-busy `while true; do :; done` to zero-CPU `tail -f /dev/null` in RelauncherTests.cs:67. (2) Null AppRestartCommand self-respawn: removed the RelaunchCommand fallback in Relauncher.cs:30-32 so a null AppRestartCommand falls through to environment-variable path instead of respawning the relauncher itself. (3) Cross-platform: added `Assert.SkipUnless(!OperatingSystem.IsWindows(), …)` skip guard on the fake-app spawn test. Also changed `sleep infinity` to `tail -f /dev/null` to pass the RealSleepGuard. Full suite: 0 failed, 3117 passed, 114 skipped, 3231 total."}

## Stage 10 - Verify

{
  "summary": "Fixed three bugs that stranded the RestartBetweenTasks drain boundary. (1) Added `AppRestartCommand` field to `RestartHandoff` (separate from `RelaunchCommand`) to carry the true app-restart argv; `MainWindowViewModel.Restart.cs` builds it via `BuildAppRestartArgs()` — source checkout gets `[\"dotnet\", \"run\", \"--project\", \"…/VisualRelay.App\", \"--\"]`, published install gets the binary path. (2) Fixed argv shape everywhere: no pre-joined strings — each element is a single token, fed individually via `ProcessStartInfo.ArgumentList`. (3) Extracted `Relauncher.RunAsync(rootPath, timeProvider?)` into `Relauncher.cs` (internal, `InternalsVisibleTo` tests) which spawns the app using `AppRestartCommand`, polls for sidecar consumption within a 30-second bounded window using `TimeProvider`, writes drain-log events (`restart-resume` on success with pid, `spawn-failed` with detail on failure), and returns 0 only on confirmed arrival — otherwise 1, leaving the sidecar in place. `Program.cs` delegates to `RunAsync`. Three new tests verify argv round-trip, fake-app spawn success (zero-CPU `tail -f /dev/null` with `ManualTimeProvider`), and nonexistent-binary failure path. Existing guards intact: bind-conflict wait, 5-minute staleness window, no-progress guard, user-pause priority. Cross-platform skip guard added on Unix-only test.",
  "commitMessages": [
    "fix: record true app-restart command in handoff — separate from relauncher spawn\n\n- Add AppRestartCommand (string[]?) to RestartHandoff\n- BuildAppRestartArgs() produces proper argv: dotnet run --project …/VisualRelay.App\n- Relauncher reads AppRestartCommand, never falls back to RelaunchCommand\n- Spawn via ArgumentList (no pre-joined strings)\n- Verify arrival with bounded polling, write drain-log events, exit non-zero on failure",
    "repair RestartBetweenTasks: relauncher now spawns the app instead of itself\n\nBug 1: handoff recorded the relauncher's own spawn command (self-respawn).\nBug 2: argv was a 2-element array with pre-joined string (single token).\nBug 3: silent exit 0 regardless of outcome, no drain-log event.\n\nAll three fixed: AppRestartCommand field, proper argv arrays,\narrival verification with drain-log reporting.",
    "fix: three bugs in RestartBetweenTasks relauncher\n\n- Separate app-restart command from relauncher-spawn command\n- Proper argv arrays end-to-end via ArgumentList\n- Bounded arrival verification + drain-log events + non-zero on failure"
  ]
}

## Stage 11 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 12 - Commit

Committed by Visual Relay.

