# Give the first-output watchdog a real liveness signal (not just a trace file)

`FirstOutputWatchdog.WaitAsync` (`src/VisualRelay.Core/Execution/ProcessRunners.Watchdog.cs`)
treats "the first filesystem entry appears in the stage's `--trace-dir`" as the sole
liveness signal: it polls `traceDir` every 200ms and disarms only when a file shows up
(`ProcessRunners.cs:91` calls it; `:82` tails the same dir; `:178` passes `--trace-dir`).

But swival writes its first trace **file only after its first turn completes**. For heavy
stages (Implement, and any long first reasoning turn on DeepSeek V4) swival is demonstrably
working — making successful proxy calls and writing repo files — for many minutes before
that first trace entry. The watchdog therefore **false-kills healthy stages**:

- Empirically (2026-06-09): during an installer-4 stage-2 "stall" the LiteLLM proxy returned
  continuous 200s and TTFT was 1.4s; parallelize advanced several stages on the same backend;
  yet the watchdog killed the stage 3× at the per-tier budget. Implement stayed false-killed
  even at a 300s budget while swival made 40+ successful proxy calls and wrote KeySetupPanel files.
- The original premise ("a healthy stage emits its first trace entry quickly", from
  `DONE-swival-first-output-watchdog.md`) holds for light stages but is **false for heavy ones**.

Current stopgap: the watchdog is disabled (`.relay/config.json` first-output budgets set above
the 20-min `SubagentTimeoutMilliseconds`, commit 41f81e8), so the 20-min cap is the only stall
backstop. That's coarse — a genuinely hung stage wastes up to 20 min, and heavy-but-healthy
stages need the cap raised (no early self-heal).

## Goal
Restore a *useful* first-output watchdog by disarming on a signal that reflects swival actually
being alive — not just a trace file — so it catches genuine pre-stream stalls in ~minutes
WITHOUT killing healthy slow-first-turn stages. Then re-enable sane per-tier budgets.

## Approach (suggested)
- swival's stdout/stderr is already captured by `ProcessCapture.RunAsync` (`ProcessRunners.cs`).
  Have `ProcessCapture` expose a "first output byte seen" signal (a `TaskCompletionSource`/token
  it trips on the first stdout OR stderr byte). Disarm the watchdog on **(first trace file) OR
  (first process output byte)** — whichever comes first. swival emits startup/skill-activation
  output early, so this disarms within seconds for any live process.
- Keep the kill+retry behaviour for the genuinely-dead case (no output of any kind within the
  per-tier window), and restore tight per-tier budgets (e.g. cheap/balanced ~120s, frontier ~660s)
  once the signal is reliable. Re-point `.relay/config.json` budgets back below the cap.
- Regression test: a fake process that writes to stdout but creates NO trace-dir file within the
  threshold MUST NOT be killed; a process that writes nothing at all MUST be killed+retried.
  (See `tests/VisualRelay.Tests/SwivalSubagentRunnerWatchdogTests.cs` for the existing harness.)

Related: memory `vr-stall-often-watchdog-false-kill`. Supersedes the trace-file approach in
`DONE-swival-first-output-watchdog.md`.
