# End a test-command run when the test process exits — don't hang on a leaked descendant's pipe

Visual Relay's verify gate kills healthy test runs at the timeout cap because it waits on the
wrong signal. The test suite finishes fine, then the wrapper sits idle until `testTimeoutMs`
fires and reports a false "test command timed out".

## What actually happened (evidence)

Several queued tasks flagged at stage 5 (Author-tests) with
`test command timed out after 600000ms`. But the captured output for those runs ends with the
suite **completing**:

```
Failed!  - Failed: 2, Passed: 1850, Skipped: 11, Total: 1863, Duration: 1 m 43 s - VisualRelay.Tests.dll
Command exited with code 1.
```

(another flagged task: `Duration: 2 m 52 s`, also `Command exited with code 1`). So `dotnet
test` ran in ~2–3 minutes and exited — then the process the harness was waiting on hung for
the remaining ~7–8 minutes until the 600 s cap force-killed it. The suite is not slow; the
**wrapper does not return when the test process ends**.

## Root cause

The driver runs the test command through `SandboxedTestRunner`
(`src/VisualRelay.Core/Execution/SandboxedTestRunner.cs`, which wraps the command in
`nono run -p vr-guard …`) → `ProcessCapture.RunAsync`
(`src/VisualRelay.Core/Execution/ProcessCapture.cs`). That method redirects stdout/stderr and
reads them asynchronously (`BeginOutputReadLine` / `BeginErrorReadLine`), then waits with:

```csharp
var waitTask = process.WaitForExitAsync(cancellationToken);
if (await Task.WhenAny(waitTask, Task.Delay(timeout, …)) != waitTask) { /* kill + timed out */ }
```

`WaitForExitAsync`, with stdout/stderr redirected and read asynchronously, does **not** complete
until the async readers hit **EOF** — and EOF only arrives once *every* process holding the
pipe's write end has closed it. When the test suite spawns a descendant that survives the test
process and inherited those write ends, the pipe stays open, `waitTask` never completes, and the
run only ends on the `Task.Delay(timeout)` branch. The process-group reap
(`KillProcessGroup`, already wired via `SetProcessGroup` at start) runs **only after**
`await waitTask` or on the timeout branch, so on the normal path it never fires.

This exact hazard is already documented in the suite: `FdLeakTests`
(`tests/VisualRelay.Tests/FdLeakTests.cs`), in
`ProcessCapture_DetachedChildReapedAfterNormalExit`, deliberately redirects its forked child's
stdio to `/dev/null` with the comment *"so it does not inherit the pipe write-ends and block
WaitForExitAsync on macOS."* Our production failure is precisely the case that test sidesteps:
a descendant that **does** inherit the pipe.

## What to build

Decouple "the test process exited" from "stdout/stderr drained", so the run ends promptly on the
former and can never block indefinitely on the latter:

- Detect the spawned process's exit independently of stream EOF (e.g. `EnableRaisingEvents` +
  the `Exited` event into a `TaskCompletionSource`, raced against the timeout) rather than
  relying on `WaitForExitAsync` alone.
- On process exit, **reap first** — `KillProcessGroup(stageGroupId)` and/or
  `Kill(entireProcessTree: true)` — so surviving descendants release the pipe, **then** drain
  stdout/stderr with a short bounded grace. If the streams still don't EOF within the grace (a
  fully detached pipe-holder), return anyway with whatever was captured. Never block to the cap
  on a clean exit.
- Leave the genuine-hang path intact: a suite that truly runs past the cap must still be killed
  and reported with the existing `test command timed out after …ms` message
  (`SandboxedTestRunner`).

## Test (write first — RED, then GREEN)

Add a `[Fact]` to `FdLeakTests` that is the inverse of the existing detached-child test: run a
script through `ProcessCapture.RunAsync` whose forked child **inherits** stdout/stderr (do NOT
redirect to `/dev/null`) and `sleep`s several seconds; the parent prints a sentinel and exits 0
immediately. Assert `timedOut == false`, that `RunAsync` returns shortly after the *parent*
exits (well under the child's sleep and far under the timeout), and that the captured output
contains the sentinel. This is RED against current code (hangs until the child's sleep ends, or
the cap) and GREEN after the reap-on-exit + bounded-drain fix. Keep
`ProcessCapture_DetachedChildReapedAfterNormalExit` green. Run with `./test.sh` (persists
logs/trx and prints failing test names).

## Environment notes (Tart VM vs host)

- This bug lives in `ProcessCapture` — pure .NET. It reproduces **without** nono and without the
  host's specific sandbox profile, so the deterministic `FdLeakTests` case above exercises it on
  the VM directly. You do **not** need to reproduce the full sandboxed-suite hang.
- Why it bit on the host but the VM usually runs clean: the production trigger is a suite
  descendant that survives `dotnet test`; whether it lingers can depend on the machine/sandbox.
  This fix makes the wrapper robust regardless, so that host/VM difference stops mattering.
- nono **is** on PATH in the VM (the `SandboxedTestRunner` path is real there), but neither the
  fix nor its test depends on nono.
- Deployment caveat: the live verify gate runs the **deployed** Visual Relay binary's
  `ProcessCapture`. This fix only protects the gate after Visual Relay is rebuilt and redeployed
  on the VM; it does not retroactively unwedge a run on the old binary. (Removing the concrete
  leak — `test-harness-2` — is what lets the gate pass on the *current* binary.)

> **Sequencing — land this first (1 → 2 → 3).** This is the foundational fix of a three-task
> group. `test-harness-2` removes a concrete listener leak; `test-harness-3` fixes an unrelated
> hermeticity bug. Both must keep the full suite green, and the broader queue's flagged tasks
> can only clear their verify gate once leaked descendants stop wedging it — this task is what
> makes any leaked descendant non-fatal. The implementer sees one task at a time; you do not need
> 2 or 3 to do this one.

## Done when

- `ProcessCapture.RunAsync` returns promptly after the spawned test **process** exits, even when
  a descendant keeps stdout/stderr open; the normal-exit path no longer blocks to the timeout.
- On process exit the process group is reaped before/while draining, and the drain is bounded so
  a detached pipe-holder cannot wedge the run.
- New inherited-pipe regression test is RED before the fix and GREEN after;
  `ProcessCapture_DetachedChildReapedAfterNormalExit` stays green.
- Genuine over-cap hangs are still killed and reported with the existing timeout message.
- `./visual-relay check` green; files under 300 lines; Conventional Commit (e.g.
  `fix(harness): end test run on process exit instead of blocking on a leaked descendant's pipe`).
