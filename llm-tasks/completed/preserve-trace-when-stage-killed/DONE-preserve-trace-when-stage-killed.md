# Preserve Stage Trace and Output When a Stage Is Killed

A watchdog-killed stage currently leaves **zero** autopsy evidence. Observed on 2026-07-06: a Fix
stage ran the full 30-minute absolute ceiling doing heavy real work (~190 model completions
through the LiteLLM proxy, an InspectCode run, 629 inserted lines across ~25 files — recovered
only via the flagged-work bundle), yet when killed it left an **empty trace directory** (no
session `.jsonl`, zero trace events in `run.log`, blank Activity pane), a killed-output autopsy
header reading `bytes: 0`, and no report JSON. The stage looked hung when it was actually
mid-flight and nearly done. Give the child a graceful-stop window so its trace/report flush before
the hard kill, and record trace presence in the autopsy artifact.

## Current state (researched)

- **Root cause is flush-at-end + quiet stdout + immediate SIGKILL** (all three verified):
  1. swival (observed version 1.0.35, external tool — not part of this repo) writes the whole
     session trace `.jsonl` **at exit**: in a healthy stage's trace file, all 319 entries carry
     write-timestamps within ~140 ms of each other, stamped at stage end; `run.log` shows each
     stage's trace events landing in a single burst at its `stage_done` time; watchdog heartbeats
     log `lastPulseSource=cpu` exclusively (a mid-stage trace pulse never fires).
  2. The driver launches swival with `-q` (`SwivalSubagentRunner.BuildArguments` in
     `src/VisualRelay.Core/Execution/ProcessRunners.cs`, which also passes
     `--trace-dir invocation.TraceDirectory` and `--report invocation.ReportFile`), so stdout
     carries nothing to capture.
  3. The kill is immediate and forceful: `src/VisualRelay.Core/Execution/ProcessCapture.cs`
     registers `killToken.Register(() => { … process.Kill(entireProcessTree: true); … })`, and the
     timeout path likewise calls `process.Kill(entireProcessTree: true)` plus a best-effort
     `KillProcessGroup(stageGroupId.Value)`. A SIGKILLed Python process runs no
     `atexit`/`finally` handlers, so the end-of-session trace dump never happens.
- **Kill orchestration** — `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs`: the
  `ActivityWatchdog` (constructed with first-output / inactivity / absolute-ceiling windows)
  cancels `watchdogCts`, which is wired as `ProcessCapture.RunAsync(..., killToken:
  watchdogCts.Token, ...)`. After the kill, `TryPersistKilledOutput(...)` (defined in
  `src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs`) writes
  `stage{n}-attempt{k}.killed-output.txt` with a header
  (`# reason: … lastSignal: … silenceMs: … bytes: N`) plus the captured output.
- **Trace tailing** — `src/VisualRelay.Core/Traces/RelayTraceTailer.cs` polls the trace directory
  with per-file offsets; its `DisposeAsync` runs a final `PollAsync` before stopping, and each
  observed entry pulses the watchdog (`onActivity: () => watchdog.Pulse("trace")` in
  `ProcessRunners.RunAsync.cs`). Because of flush-at-end, those pulses are inert today; a
  graceful flush on kill (or upstream streaming) would also make the Activity pane and the
  inactivity watchdog see real signal.
- **Sandbox wrapper** — the child is not swival directly: `BuildLaunchTarget` wraps it under
  `nono run` (see the comment block above `BuildArguments` in `ProcessRunners.cs`). Signals must
  reach the *process group* (the `KillProcessGroup` plumbing and `stageGroupId` already exist in
  `ProcessCapture.cs`) so the Python child receives them through the wrapper.
- **Existing test surfaces** — `tests/VisualRelay.Tests/ProcessCaptureConcurrencyTests.cs` and
  `ProcessCaptureEnvStripTests.cs` (process mechanics);
  `SwivalSubagentRunnerWatchdogTests.cs` / `.ActivityWatchdog.cs` / `.TierWindows.cs` and
  `ActivityWatchdogSocketWedgeTests*.cs` (kill classification, killed-output persistence).

## What to build (TDD-first)

1. **Diagnose swival's options first.** Check `swival --help` (and its docs/config) for an
   incremental-trace / streaming / flush-interval flag. If one exists, adopt it in
   `BuildArguments` (test: the argument list includes it) — streamed traces make kills lossless
   by construction *and* revive live trace pulses. Implement the graceful stop below regardless
   (it also protects the report file and covers swival versions without the flag).

2. **Graceful stop before hard kill** in `ProcessCapture.cs` (put new logic in a new partial,
   e.g. `ProcessCapture.GracefulStop.cs` — the main file is at 299/300):
   - On kill-token fire and on the internal timeout path (non-Windows only; Windows keeps the
     current immediate kill), first send **SIGINT to the process group**, then wait a bounded
     grace window (~10 s) for voluntary exit; only then fall through to the existing
     `Kill(entireProcessTree: true)` + `KillProcessGroup` path. A Python child receiving SIGINT
     raises `KeyboardInterrupt`, unwinding through `finally`/`atexit` — which is exactly the
     flush-at-end path that writes the trace `.jsonl` (and possibly the report).
   - The grace window must NOT change what is reported: the watchdog outcome
     (`FiredAbsoluteCeiling` etc.), `lastSignal`, and `silenceMs` are captured when the watchdog
     fires; the reported timeout stays the configured ceiling, not ceiling + grace.
   - Tests first (mirror `ProcessCaptureConcurrencyTests.cs` style): a child script that traps
     INT, writes a marker file, and exits — assert the marker exists and no hard kill was needed;
     a child that ignores INT — assert it is still force-killed shortly after the grace window;
     Windows path unchanged.

3. **Autopsy records trace presence.** Extend `TryPersistKilledOutput` so the header also reports
   the trace directory's state at persist time (e.g. `traceFiles: 1  traceBytes: 48213` or
   `traceFiles: 0`), making "flushed on grace" vs "lost" legible in future autopsies. Ensure the
   ordering in `ProcessRunners.RunAsync.cs` lets the tailer's final poll run **after** the graced
   exit so flushed entries still publish to `run.log` (the tailer's `DisposeAsync` final
   `PollAsync` handles this if disposal happens after the process task is awaited — verify, and
   adjust only if the order is wrong). Update the watchdog-kill tests asserting the header shape.

## Done when

- A stage killed at the absolute ceiling with a signal-honoring child leaves a non-empty session
  `.jsonl` in its `stage{n}-attempt{k}/` trace directory, its trace entries appear in `run.log`,
  and the killed-output header reports the trace file count/bytes.
- A child that ignores SIGINT is still force-killed within the grace window, with today's
  classification and artifacts (plus the new trace-presence header fields).
- Watchdog outcomes, retry/escalation behavior, and reported timeout values are byte-for-byte
  unchanged apart from the new header fields.
- `./visual-relay check` passes (file-size guard, format verification, build, full test suite,
  README screenshot render).

## Guardrails

- Conventional Commits only (the `commit-msg` hook enforces the full ruleset). See
  `docs/commit-messages.md` and `AGENTS.md`.
- 300-line ceiling (`tools/VisualRelay.Guards`): **`ProcessCapture.cs` is at 299,
  `ProcessRunners.Watchdog.cs` at 297, `ProcessRunners.RunAsync.cs` at 290** — new logic goes in
  new partials (`ProcessCapture.GracefulStop.cs`); header changes in `ProcessRunners.Helpers.cs`
  (225) have headroom.
- Never weaken the sandbox: no new nono flags, no bypasses — this task only changes *how* the
  child is stopped and what is persisted afterwards.
- Do not raise or reshape any timeout values here; the grace window is additive shutdown
  behavior, not a budget change.
- Changes to swival itself are **out of scope** (it is an external tool); adopting an existing
  swival CLI flag is in scope.
- Tests must not depend on real `~/.config` writes or a live model backend (the nono-sandboxed
  suite denies them) — use `TestRepository`/temp-dir fixtures and fake child scripts like the
  existing ProcessCapture tests.
- Minimal diffs: change only what this task needs; do not reformat or reflow unrelated code.
