# Harness: bound file-descriptor usage across subprocess spawns

Nothing in this task is language-, runtime-, or filesystem-specific. All fixes
are in VR's own infrastructure (ProcessCapture, ProcessTreeCpuSampler,
GitInvoker) and apply regardless of the target repo's toolchain.

## Current state (researched)

### The crash

A 3-task batch drain (one task ~60 min) crashed with:

```
System.IO.IOException: Too many open files
  at RelayDriver.FlagAsync → Directory.CreateDirectory
```

Single-task runs (28–36 min) never hit this. The leak scales with runtime and
subprocess count, not task count alone.

### Ranked leak sources

**1. `ProcessTreeCpuSampler.TrySampleTreeCpuMs` — volume driver (HIGH confidence)**

`src/VisualRelay.Core/Execution/ProcessTreeCpuSampler.cs` lines 15–38.

`using var ps` is correct and `ReadToEnd()` drains stdout. The concern is
volume: `ps.StandardOutput.ReadToEnd()` is synchronous. `SampleTreeCpuLoopAsync`
(`ProcessCapture.cs:140`) calls this from a `Task.Delay`-resumed ThreadPool thread,
so each sample blocks that thread until `/bin/ps` exits. With multiple concurrent
stages and a 4-second interval, slow `ps` runs (100–2000 ms under load) can overlap,
accumulating pipe FDs across threads. The `using` ensures they close, but the
steady-state concurrency grows with `concurrent_stages × (ps_runtime / interval)`.

The `try { ps.Kill(...) } catch` on timeout does not call `ps.StandardOutput.ReadToEnd()`
before killing, leaving the pipe potentially unread when the kill path runs. Adding
`ps.StandardOutput.ReadToEnd()` before `WaitForExit` on all paths makes disposal robust
to exceptions and ensures the pipe is always drained.

**2. `GitInvoker` resolution helpers — bounded startup leak (LOW severity)**

`src/VisualRelay.Core/Execution/GitInvoker.cs:136–158` (`EnsureResolved`) caches
`_gitBinary` in a static field with double-checked locking. The resolution helpers
(`ResolveViaXcrun`, `ResolveViaCommandV`, `ProbeGit`) run **only once at startup**,
not per `RunAsync`. The leak is therefore at most 3 `Process` objects × up to 3
FDs each = ~9 FDs leaked until GC finalization — bounded, not a volume driver.

Fix: add `using` to those 3 `Process` objects. Minor hygiene, correct regardless.

**3. Detached child-process subtrees — general resource leak (MEDIUM confidence)**

Any stage script can spawn persistent children (build daemons, file watchers,
language servers, test harness processes) that detach and reparent to PID 1 when
the stage process exits normally. These survivors are invisible to VR and leak
file descriptors and memory indefinitely.

`ProcessCapture.RunAsync` already kills `entireProcessTree:true` on **timeout**
(`ProcessCapture.cs:104–106`) but does **not** reap on **normal exit**. On normal
exit only the stage leader dies; detached descendants survive.

Fix: run each stage process in its own process group; after the stage exits (normal
or timeout), kill that group to reap any detached survivors.

**CRITICAL SAFETY constraint**: up to 10 stages run concurrently during parallel
planning. The reaping must be scoped to exactly **this stage's process group**
and must never touch another stage's group or unrelated host processes (e.g. the
model backend). Process group ID = the stage leader's PID (set via
`UseShellExecute=false` + `process.StartInfo.CreateNoWindow = false` or an
explicit `setpgrp` before exec on POSIX). Kill signal must target
`-processGroupId`, not `-1` (broadcast).

## What to build

### 1. Fix `ProcessTreeCpuSampler` to be robust on all exit paths

In `TrySampleTreeCpuMs`:

- Drain `ps.StandardOutput.ReadToEnd()` **before** `WaitForExit` on both the
  normal and timeout paths. (The normal path already does this; the kill path
  currently skips it.)
- On the timeout path, after `ps.Kill(entireProcessTree:true)`, call
  `ps.StandardOutput.ReadToEnd()` to drain any buffered output before the
  `using` scope disposes `ps`. This ensures pipe handles are released promptly.
- The `using var ps` already guarantees `Dispose`; this change makes the
  timing deterministic rather than GC-dependent.

### 2. Add `using` to `GitInvoker` resolution helpers

`GitInvoker.cs` — wrap the `new Process()` in `ResolveViaXcrun` (line ~201),
`ResolveViaCommandV` (line ~234), and `ProbeGit` (line ~267) in `using` blocks.
This is a hygiene fix; these run once and the leak is bounded (~9 FDs), but
deterministic disposal is always correct.

### 3. Reap each stage's detached child-process subtree on normal exit

In `ProcessCapture.RunAsync` (`src/VisualRelay.Core/Execution/ProcessCapture.cs`):

- Place the stage process in its OWN process group so detached grandchildren can be
  reaped as a group. Use a CROSS-PLATFORM, IN-PROCESS approach — do NOT shell out to an
  external `script`/`setsid`/`setpgrp` wrapper binary. (That approach is macOS/Linux-
  availability-specific and was REVERTED before for breaking on Linux; a racy parent-side
  setpgid was also a problem. Do not reintroduce either.) Specifically:
  - Set `process.StartInfo.UseShellExecute = false` (already set).
  - On POSIX ONLY (guard EVERY native call with `RuntimeInformation.IsOSPlatform(OSPlatform.Linux)`
    `|| RuntimeInformation.IsOSPlatform(OSPlatform.OSX)`): immediately after `process.Start()`,
    call `setpgid(childPid, childPid)` via `[DllImport("libc")]` P/Invoke, BEST-EFFORT —
    ignore a non-zero/errno result (the child may have already exec'd; accept that tiny
    start-race rather than a fragile wrapper). The group id is `childPid`.
  - On Windows: do NOT call setpgid; rely SOLELY on `Process.Kill(entireProcessTree: true)`
    (it uses a Job Object) and SKIP the POSIX group-kill path below.
- After `await waitTask` (normal exit path, `ProcessCapture.cs:109`), kill the
  stage's process group to reap any survivors:
  ```csharp
  try { KillProcessGroup(stageGroupId); } catch { /* best-effort */ }
  ```
- The timeout/kill path already calls `process.Kill(entireProcessTree:true)`;
  supplement it with the group kill for processes that reparented before kill.
- **Safety**: store the process group ID immediately after `process.Start()`.
  Only kill that specific group ID. Never kill group 0 (caller's group) or -1.
  Gate the kill on `stageGroupId != Process.GetCurrentProcess().SessionId`
  as a sanity check.

## Regression test

Add a test (portable, language-agnostic) that:

- Spawns a stage process via `ProcessCapture.RunAsync` that itself forks a
  detached child (e.g. a shell one-liner that starts a background sleep and
  exits immediately).
- After `RunAsync` returns, asserts that the detached child is no longer in the
  process table (killed by group reap).
- As a handle-count smoke check: calls `TrySampleTreeCpuMs` N=50 times in a
  loop; asserts that the open-handle delta across the loop is < 10 (rules out
  a per-call leak while allowing GC jitter).

Test file: `tests/VisualRelay.Tests/FdLeakTests.cs`; must stay under 300 lines.

## Done when

- **Write failing tests first**: `ProcessTreeCpuSampler_DrainedOnAllPaths` and
  `ProcessCapture_DetachedChildReapedAfterNormalExit` are confirmed red before
  any source changes.
- **`ProcessTreeCpuSampler.cs`**: `ReadToEnd()` is called on all exit paths
  (normal and kill-on-timeout) before `WaitForExit`. The fix must not break the
  async CPU-sampling loop contract (`SampleTreeCpuLoopAsync` in `ProcessCapture.cs:140`).
- **`GitInvoker.cs`**: all three resolution helpers wrap their `Process` in `using`.
- **`ProcessCapture.RunAsync`**: stage runs in its own process group; group is
  killed after normal exit (best-effort, silent on failure).
- **Safety invariant verified**: the process-group kill is scoped to the stage's
  own group ID; no other concurrent stage or host process is affected.
- **Handle-count test green**: delta < 10 across 50 `TrySampleTreeCpuMs` calls.
- **`./visual-relay check` is green** — all pre-existing tests pass unmodified.
- **Files stay under 300 lines each.** Test file is a single focused file.
- **Conventional Commit** subject candidates:
  - `fix(sampler): drain ps pipe on all exit paths to bound FD count`
  - `fix(git-invoker): dispose resolution Process objects (startup-only hygiene)`
  - `fix(capture): reap stage's detached child-process group on normal exit`

## PITFALL — keep new tests FAST (this task previously FLAGGED on a 10-min suite timeout)
The new ProcessCapture FD/reap tests spawn real subprocesses and MUST keep the WHOLE `dotnet test`
suite well under the 10-min `testTimeoutMs`. The prior attempt timed out because a test used ~40
iterations each blocking on a multi-second `WaitForExit` timeout. Therefore:
- Use the SHORTEST sleeps/timeouts that still reproduce the bug (spawn `sleep 0.3`; bound every wait
  to <=1s); never block on a default/long per-iteration timeout.
- Use as FEW iterations as needed for a detectable FD delta (a handful, NOT 40).
- Redirect ALL spawned-child stdio away from the captured pipes so no descendant inherits the pipe
  write-end (the macOS WaitForExitAsync hang); bound every wait — no unbounded waits.
- After writing, run ONLY the new test class and confirm it finishes in SECONDS; the full suite must
  stay well under 10 min.
