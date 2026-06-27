# Stall/ceiling kills discard the attempt's captured output — persist it for autopsy

When the watchdog fires, ProcessCapture kills the tree and the attempt's accumulated
stdout/stderr (already captured line-by-line into the in-memory buffer via
OutputDataReceived/ErrorDataReceived) is dropped. The 2026-06-10 triple-stall autopsy
on fix-jobs-non-us-location had literally zero evidence to read: empty trace dir, no
report.json, no stderr text — only `lastSignal: stderr` hinting the process said
*something* once. Root-causing filesystem-freeze vs provider-hang vs swival-crash took
proxy-log archaeology instead of one file read.

## Goal

Every abnormally-ended attempt (stall kill, absolute-ceiling kill, nonzero exit
without report) leaves its captured combined output on disk next to the other attempt
artifacts — e.g. `stageN-attemptM.output.txt` — and the kill/flag event line includes
the byte count and the persisted path. Normal successful attempts keep today's
behavior (no extra file). Works for any target repo; the write must not throw the
driver if the path is unwritable (best-effort, log a warn).

## Approach (suggested)

- The capture buffer already exists in ProcessCapture; on the kill paths (and on
  nonzero-exit-no-report), write it out before returning the stall result. Keep the
  existing buffer cap/truncation semantics if any; note truncation in the file header.
- Include a small header: stage, attempt, start time, kill reason, per-source
  last-pulse info if available.
- Tests: simulate a stall kill with buffered output → file exists with content and
  event carries the path; successful run → no file; unwritable dir → warn, no throw.

---

CLOSED 2026-06-10 by escape-hatch hand fix (TDD in /tmp worktree): VisualRelay could
not process this through its own pipeline because the pipeline itself was being
false-killed by this very bug (fs-blinded watchdog). Implemented with the
companion change in one commit; see ProcessTreeCpuSampler, ProcessCapture
cpu sampling, SwivalSubagentRunnerWatchdogTests.CpuPulse.
