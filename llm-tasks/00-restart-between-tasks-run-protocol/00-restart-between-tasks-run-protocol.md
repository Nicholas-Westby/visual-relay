## Task: Add a "Restart Between Tasks" Run All protocol that relaunches the app after each task

Visual Relay is developed with Visual Relay: a queue of compounding tasks is
run against this repo, where each task's fix only helps the NEXT task if the
app is rebuilt and relaunched between them. Today a drain runs the same binary
end-to-end — the 2026-07-15 drain log shows every task executing under
`version=0.76+cfcc78d…` even as those very tasks sealed newer commits, so
fixes landed mid-drain were never live for the tasks that followed. Add a
third Run All protocol, **Restart Between Tasks**: identical to Sequential,
except that after a task seals its commit the app persists a handoff, quits,
relaunches itself (which recompiles from source), reopens the same repo, and
continues the queue where it left off.

### Current mechanics (verified)

- `RunAllMode` enum (`src/VisualRelay.Core/Queue/RunAllMode.cs`) has
  `Standard` and `Sequential`; the dropdown options come from
  `MainWindowViewModel.Properties.cs:12` and render in
  `src/VisualRelay.App/Views/Controls/TopBar.axaml:124` (a plain ComboBox with
  a tooltip describing the two modes).
- `RelayQueueController.cs` already has per-task-boundary checkpoints: pause
  is honored between tasks (lines ~208/223) and Sequential collects new tasks
  at each boundary (line ~271). The restart handoff belongs at this same
  boundary.
- Flagged tasks get a `NEEDS-REVIEW` marker under `.relay/<task>/` and
  queue collection already filters `!t.NeedsReview`
  (`RelayQueueController.PrivateHelpers.cs`, `CollectNewTasks`); completed
  tasks move to `llm-tasks/completed/`. A fresh drain therefore naturally
  runs only the remaining pending tasks — the continuation mechanism should
  lean on that rather than inventing a parallel queue snapshot.
- Launch path: the `./visual-relay` bootstrap execs the C# CLI
  (`tools/VisualRelay.Cli`) via `dotnet run`, so a source-checkout relaunch
  IS the recompile. Published (brew) installs exec a prebuilt binary — the
  protocol must still work there (restart without recompile, harmless).
- The app binds the loopback control API and **fails loud on bind conflict**
  (completed task `expose-instance-identity-and-fail-loud-on-bind-conflict`),
  so the replacement instance must not start until the old instance has fully
  exited. The litellm backend on `127.0.0.1:4000` is a separate process and
  survives restarts; the new instance reconnects to it.
- `RootPath` is not persisted anywhere today (the app opens on the launch
  directory) — the handoff must carry it explicitly or the restarted app will
  not be pointed at the repo the run was working on.

### What to build

1. **Protocol plumbing**: add `RunAllMode.RestartBetweenTasks`; it inherits
   Sequential semantics everywhere Sequential is special-cased (e.g.
   `skipPlanning`, boundary new-task collection).
2. **Drain behavior**: in this mode, after a task completes at the boundary
   checkpoint:
   - Task **sealed a commit** → write the handoff sidecar (see 3), stop the
     drain cleanly, and trigger the relaunch. The restart exists to load the
     new code; a task that committed nothing (flagged) changed no code, so
     **continue in-process to the next task instead of restarting** — this,
     plus the NEEDS-REVIEW skip, is the double guard against a flagged task
     causing a restart loop.
   - Restart after the final committed task too, so the session ends on the
     freshest build; the relaunched app then finds an empty pending queue and
     settles idle.
3. **Handoff + relaunch**: a sidecar (e.g. `.relay/restart-handoff.json`)
   recording rootPath, mode, drain id, timestamp, and the remaining pending
   count. Relaunch mechanics: spawn a detached relauncher that waits for the
   current process to exit (control-port bind conflict otherwise), then starts
   the app the same way it was started (source checkout → the recompiling
   path; published → the binary). On startup, when a fresh sidecar is present:
   reopen its rootPath, delete-or-mark the sidecar, and auto-continue Run All
   in RestartBetweenTasks mode. Guards: a stale sidecar (old timestamp, or
   rootPath missing) is discarded loudly, never auto-run; if a restart cycle
   completes zero tasks, end the run with a clear drain-log event instead of
   restarting again; a user pause always wins over auto-continue.
4. **Custom dropdown**: replace the plain ComboBox items with a two-line item
   template — protocol name, and beneath it one muted explanatory line for
   ALL three protocols (e.g. Standard: "Plan all tasks up front, then
   execute"; Sequential: "One task at a time, checking for new tasks between";
   Restart Between Tasks: "Sequential, plus the app rebuilds and relaunches
   after each committed task — for repos that build Visual Relay itself").
   Requirements: the collapsed control shows only the compact protocol name
   (two-line template must not bloat the closed state); the expanded popup is
   wide enough that "Restart Between Tasks" and every description render
   without truncation; description line uses the centralized theme
   colors/font sizes with accessible contrast in both themes, not a
   hard-coded gray; keyboard navigation and screen-reader naming
   (`AutomationProperties`) keep working; update the tooltip at
   `TopBar.axaml:124` to cover all three modes.
5. **Observability**: drain-summary events for the handoff
   (`restart-handoff`, with next pending task) and for startup continuation
   (`restart-resume`), each stamped with the running `version=…+sha` so logs
   prove the recompile happened; `/state` exposes the selected mode and a
   pending-handoff indicator so the control API can follow a restarting run.

### Constraints

- Repo-agnostic: the protocol must work pointed at any repository; nothing may
  assume the target repo is Visual Relay (self-hosting is the motivation, not
  a precondition).
- Standard and Sequential behavior must be byte-for-byte unchanged.
- No real-time sleeps in tests (TimeProvider patterns per
  `virtualize-watchdog-test-waits`); keep files under the 300-line guard.
- The relauncher must never leave two live instances or zero instances
  silently: bind-conflict remains fail-loud, and a failed relaunch (e.g. the
  new build does not start) must leave the sidecar plus a loud drain-log
  event for diagnosis rather than retry-looping.

### Tests (red first)

- Controller test: RestartBetweenTasks + a task that seals a commit → drain
  stops at the boundary with a handoff written; a flagged task → no handoff,
  drain proceeds to the next task in-process.
- Startup-continuation test: fresh sidecar + a queue containing one completed,
  one needs-review, and one pending task → run auto-continues with only the
  pending task, needs-review is skipped; stale sidecar → discarded, no
  auto-run.
- No-progress guard: a continuation cycle that completes zero tasks ends the
  run (no second handoff written).
- Headless UI test: dropdown popup contains all three items, each rendering
  name + description; the collapsed selection box shows the name only.

### Verification

- `./visual-relay check` fully green including the new tests.
- Manual self-hosted smoke: point at this repo, queue two trivial tasks, run
  with Restart Between Tasks, and confirm via drain logs that the second task
  ran under a different `version=…+sha` than the first.
