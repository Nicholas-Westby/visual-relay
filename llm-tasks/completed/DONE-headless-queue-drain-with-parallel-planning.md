# No headless access to the two-phase queue drain (parallel planning is GUI-only)

`RelayQueueController.DrainAsync` supports the two-phase drain — Phase 1 runs planning
stages 1–4 for all pending tasks concurrently in isolated worktrees (bounded by
`MaxPlanConcurrency`), Phase 2 executes stages 5–11 serially — but the only caller that
wires the two-phase constructor is the Avalonia GUI (`MainWindowViewModel.Execution.cs`,
`DrainQueueAsync`). Headless/CI/automation users only have
`tools/VisualRelay.RunTask`, which is one task per invocation, fully serial, so they
pay full wall-clock for planning that could overlap. Driving a many-task queue from a
script today means a bash loop over RunTask with none of the drain's queue semantics
(circuit breaker, NEEDS-REVIEW continuation, drain summary log).

## Goal

A headless console tool drains a target repo's queue with the same two-phase semantics
the GUI gets: `dotnet run --project tools/VisualRelay.DrainQueue -- <root> [taskId ...]`.

- No taskIds → drain every pending (non-NEEDS-REVIEW) task in repository order.
- With taskIds → drain exactly those tasks in the given order (any id not among the
  pending set is a usage error: print which, exit 2, touch nothing).
- Phase 1 planning runs in parallel per `MaxPlanConcurrency` from the TARGET root's
  config; Phase 2 executes serially in queue order. Works for any target repo
  (the target's `.relay/config.json` governs; nothing about the target may be assumed
  .NET or otherwise).
- Console output stays attributable while planning interleaves: every event line is
  prefixed with its task id. Each task also keeps its `.relay/<taskId>/run.log` file
  sink as the GUI path does.
- Per-task outcome lines as RunTask prints them (`Status: taskId sha-or-reason`), plus
  a final one-line summary (committed/flagged/failed/planned counts).
- Exit code 0 when nothing flagged or failed (an empty queue is a successful no-op
  that says so); exit 2 when any task flagged/failed or the drain halted.

## Approach (suggested)

- New `tools/VisualRelay.DrainQueue` console project mirroring RunTask's shape: load
  config via `RelayConfigLoader`, build the two-phase `RelayQueueController` exactly as
  the GUI does — per-task `SwivalSubagentRunner` factory + `ShellTestRunner` for
  planning, and an `IRelayTaskRunner` for Phase 2 that mirrors `GuiTaskRunner`
  (RelayDriver, `CreateGitCommit: true`, `Resume: true` — Resume is what makes Phase 2
  pick up Phase 1's completed stages instead of redoing them).
- Subset/order selection: after `RefreshAsync`, rewrite the controller's `Tasks`
  collection to the requested subset/order before `DrainAsync` (or equivalent).
- Keep Program.cs thin; put exit-code mapping, argument validation, and summary
  formatting in small testable units with unit tests (outcome-set → exit code; unknown
  id rejection; ordering preserved). Existing controller behavior needs no changes —
  if a seam is missing, prefer adding the seam over duplicating drain logic.
- Acceptance smoke: running the tool against a root whose queue is empty exits 0 with
  a clear "nothing pending" message.
