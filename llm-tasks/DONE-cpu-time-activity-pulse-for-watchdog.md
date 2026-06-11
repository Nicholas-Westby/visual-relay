# Watchdog kills healthy agents when filesystem signals freeze — add a CPU-time pulse

Proven 2026-06-10 ~20:10–20:45 across two concurrent drives (JobFinder s6 ×3 attempts,
self-hosted s10): the inactivity watchdog stall-killed subagents that were demonstrably
WORKING — the LiteLLM access log shows their steady chat/completions throughout the
"silent" windows, and one killed-then-retried stage's trace file later surfaced with
242 KB of events whose mtime was frozen at a timestamp BEFORE its own process start.
Mechanism: both repos live on a Tart virtio-fs share; under host-side I/O activity the
guest's view of freshly created trace directories/files goes stale (the known
dentry/attr-cache quirk), so the watchdog's two pulse sources — process stdout/stderr
bytes (quiet in `-q` mode after startup) and trace-dir growth (frozen view) — both go
dark while the agent works. Result: healthy 30-minute implement stages burned to
stall-flags (3 × 600 s + kill overhead), and `Kill(entireProcessTree)` then hung
`WaitForExit` ~11 min on a process stuck in uninterruptible fs I/O.

## Goal

The watchdog has at least one activity signal that no target-repo filesystem behavior
can freeze: a working subagent (accruing CPU in its process tree — tool calls, JSON
parsing, streaming reads) is never inactivity-killed even when trace-file views and
stdout are completely dark. A truly wedged subagent (blocked at byte 0 on a dead
call, ~zero CPU) is still killed exactly as today. Strictly additive: a new pulse
source ORed with the existing ones; no timeout semantics change.

## Approach (suggested)

- Each watchdog poll tick (already every ≤200 ms), sample cumulative CPU time of the
  spawned process and its descendants (walk children once per tick or per few ticks;
  `Process.TotalProcessorTime` per pid, summed; the tree already gets enumerated for
  entire-tree kill). Delta above a small epsilon since the last sample → pulse with
  source "cpu".
- Epsilon/jitter: an idle-blocked process can still accrue scheduler dust; pick a
  threshold like ≥50 ms CPU delta per sample window so true byte-0 wedges stay below
  it. Make it a constant, not config.
- The stall_kill event should report per-source last-pulse ages (stdout/stderr/trace/
  cpu), not just one lastSignal — that line is the primary stall autopsy artifact.
- Tests: watchdog unit tests already simulate pulse sources; add cpu-source cases
  (pulse resets deadline; zero-delta does not; kill event carries per-source ages).
  Process-tree CPU sampling itself can be faked behind the same seam the tests use.

---

CLOSED 2026-06-10 by escape-hatch hand fix (TDD in /tmp worktree): VisualRelay could
not process this through its own pipeline because the pipeline itself was being
false-killed by this very bug (fs-blinded watchdog). Implemented with the
companion change in one commit; see ProcessTreeCpuSampler, ProcessCapture
cpu sampling, SwivalSubagentRunnerWatchdogTests.CpuPulse.
