# Harness/test-quality: guard against sync-over-async deadlocks in headless-UI tests

## Problem

The Author-tests stage agent repeatedly authored Avalonia **headless-UI tests that
deadlock** — a blocking `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` on the
single-threaded headless dispatcher whose continuation needs that same thread (a classic
sync-over-async deadlock). Such a test hangs forever; with `--blame-hang` it's now killed
at 60s (commit `1b1e556`) and reported, but the agent kept re-introducing the pattern,
which **blocked `remove-duplicate-task-title`** from getting past Author-tests — the agent
burned the whole stage ceiling re-trying a deadlocking test it couldn't get clean.

The existing `TaskDetail*` tests show the **correct** non-blocking pattern (they run in ~ms,
no hang). The agent improvising sync-over-async is the failure.

## What to do
Add a **static test-quality guard**, modelled precisely on the no-real-sleeps guard
(`tools/VisualRelay.Guards/RealSleepGuard.cs` + the `RealSleepGuardTests` meta-test + the
live-tree gate). It should fail the build when a test blocks on async on the UI thread —
e.g. `.Result`, `.GetAwaiter().GetResult()`, or `.Wait()` applied to a `Task` in a
`[Fact]` / `[AvaloniaFact]` test body, especially around `Dispatcher.UIThread` / `RunJobs` /
`window.Show()`. Tune to avoid false positives on legitimate non-UI `.Result` use; provide a
`// vr-allow-sync-over-async: <reason>` suppression comment (mirroring the sleep guard's
`// vr-allow-sleep:`).

This catches the deadlock at guard time (fast, named) instead of via a 60s blame-hang, and —
more importantly — gives the authoring agent a crisp, early "you wrote a deadlocking test"
signal it can act on, so a task like remove-duplicate stops stalling.

## Done when
- The guard flags a sync-over-async UI-test deadlock and is wired into `./visual-relay
  check`.
- The existing suite passes it (the correct `TaskDetail*` pattern is clean); a deliberately
  deadlocking sample is caught.
- General: the guard keys on the code pattern, not VR-specific symbols, so it ports to any
  Avalonia/UI codebase.

## Coordination
Distinct from `harness-verify-survives-sandbox-self-tests` (subprocess wedges in the verify)
— this is sync-over-async deadlocks at authoring time. Relevant commit: `1b1e556`
(blame-hang, which currently catches these at 60s).
