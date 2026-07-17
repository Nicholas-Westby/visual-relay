## Task: Hold flagged tasks out of Restart Between Tasks drains

Run All deliberately re-attempts flagged ("Needs review") tasks — commit
0dc9408 (2026-06-23) loads them during refresh and drains them, so an
errored/flagged task gets another chance on the next Run All. That is
acceptable for Standard and Sequential, where a drain is one bounded pass:
each queue entry runs at most once, and a task that flags again just returns
to the review pile.

Restart Between Tasks breaks that boundedness. Every committed task ends the
cycle and the relaunched app starts a FRESH drain with a fresh
`Tasks.ToList()` queue — so a flagged task is re-attempted in EVERY cycle.
A task that always flags gets re-run (at up to watchdog-ceiling cost) once
per remaining committed task; ranked early, it delays every real task by
that much; and a task that both seals and flags (the commit-gate
flag-after-seal family) can sustain a genuine self-perpetuating restart
loop: seal → restart → re-attempt → seal → restart. In this mode a flag must
mean HOLD: only an explicit human Resume or Reset re-queues the task.

### Evidence (verified)

- `RelayQueueController.DrainAsync` builds the initial queue as
  `var queue = Tasks.ToList()` — no NeedsReview filter — and `RefreshAsync`
  loads flagged tasks into `Tasks` (both since 0dc9408; its test
  `DrainQueueToolTests.ValidateTaskIds_NeedsReviewTask_IsStillPendingAndRecognized`
  documents "NEEDS-REVIEW tasks are included during RefreshAsync so
  'Run All' can re-attempt them").
- The `!t.NeedsReview` filter exists only in `CollectNewTasks`
  (`RelayQueueController.PrivateHelpers.cs`), which guards mid-drain
  ADDITIONS at boundaries — not the starting queue of each restart cycle.
- `RelayDriver.RunTaskAsync` (`RelayDriver.cs:34`) deletes the task's
  NEEDS-REVIEW marker unconditionally at run start, so each re-attempt also
  erases the hold silently (no archive; contrast task 10's
  archive-don't-delete Reset).
- Field occurrence, 2026-07-17: `hoist-pipeline-test-shared-setup`, flagged
  since 2026-07-15, was auto-re-attempted by the 05:53:16 UTC restart-cycle
  drain the moment it reached the queue head (`drain-20260717055316.log`;
  `stage7-attempt2` artifacts). It happened to pass and seal (502556a) — a
  chronic flagger in the same position would have re-run every cycle.
- Task 00's own spec intended "flagged tasks skipped via NEEDS-REVIEW" as
  the restart protocol's anti-loop guard, and
  `RelayQueueControllerRestartTests.cs` asserts a needs-review task is
  skipped — but only via the boundary-collection path; the initial-queue
  path is unguarded, so the shipped protocol does not deliver the spec's
  guarantee.

### What to build

1. **Mode-scoped hold**: when a drain runs in `RunAllMode.RestartBetweenTasks`
   (whether started from the Run All button or by the startup
   auto-continuation in `App.TryAutoResumeFromHandoff`), needs-review tasks
   are excluded from the drain queue at build time. They must not be
   started, and their `.relay/<id>/` state (marker included) must not be
   touched. Standard and Sequential keep today's re-attempt behavior
   unchanged.
2. **Covers mid-chain flags too**: a task that flags during cycle N must not
   be re-attempted by cycles N+1… of the same restart chain — the same
   queue-build exclusion applies to every continuation cycle.
3. **Observability**: when an RBT queue build excludes flagged tasks, write
   one drain-log event naming the count and ids (e.g.
   `skipped-needs-review n=2 ids=…`), so an operator can see why a flagged
   task didn't run. Update the Restart Between Tasks dropdown description
   and/or tooltip only if it currently implies flagged tasks are attempted.
4. **Explicit re-runs still work**: Resume and the task-10 Reset button
   remain the (only) ways to run flagged work in this mode — Reset returns
   the task to Pending, after which the next cycle picks it up normally.

### Constraints

- Do not change Standard/Sequential semantics (0dc9408's behavior is
  deliberate and stays), the boundary `CollectNewTasks` filter, or the
  run-start marker delete for tasks that DO run.
- Repo-agnostic; keep files under the 300-line guard; TimeProvider patterns
  for any test waits.

### Tests (red first)

- RBT drain whose task list contains a pre-flagged task: the flagged task is
  never started, its marker and state dir are untouched, the skip event is
  in the drain log — fails today (it runs when it reaches the head).
- Same setup in Sequential mode: the flagged task IS re-attempted
  (regression-pin of 0dc9408).
- Restart-chain regression: an always-flagging task plus tasks that seal —
  across the full chain the flagger runs at most once (the cycle in which it
  first flags, if it entered as pending) and never again in later cycles.
- Startup auto-continuation with a fresh handoff and a flagged task in the
  list: continuation runs only non-flagged pending tasks.
- After a task-10 Reset of a flagged task, the next RBT cycle runs it (the
  hold applies to flagged state, not to the task id).

### Verification

- `./visual-relay check` fully green including the new tests.
- Manual: flag a scratch task, start Restart Between Tasks with one other
  trivial task queued; confirm the flagged task never runs across the chain
  and the skip event appears in each cycle's drain log.
