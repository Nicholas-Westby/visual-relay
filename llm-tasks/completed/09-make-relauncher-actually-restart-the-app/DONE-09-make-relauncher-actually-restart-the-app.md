## Task: Make the RestartBetweenTasks relauncher actually restart the app

The first live RestartBetweenTasks boundary stranded the drain: on 2026-07-16
task `01-unquote-git-paths-for-non-ascii-filenames` sealed at 19:59:05Z, the
handoff was written, the app quit — and nothing ever came back. The sidecar
sat unconsumed for over an hour, no process survived, and the drain log's
last line is the `restart-handoff` event. Recovery required a human to
freshen the sidecar and relaunch by hand, once per boundary.

### Evidence (verified)

- Drain log `.relay/drain-20260716192649.log`: `restart-handoff (pending=7)`
  at 19:59:05.45, then nothing — no `restart-resume`, no failure event.
  The on-disk `.relay/restart-handoff.json` (mtime 12:59 local) was never
  consumed; `ps` shows no app or relauncher process an hour later. The
  relauncher DID build and run (its `bin/Debug` outputs are stamped 12:59).
- **Bug 1 — the handoff records the wrong command.**
  `MainWindowViewModel.Restart.cs:29-35` rewrites the handoff with
  `RelaunchCommand = [Process, Arguments]` — the pair used to spawn the
  RELAUNCHER (`["dotnet", "run --project …/VisualRelay.Relauncher -- --parent-pid …"]`,
  confirmed in the stranded sidecar). But the relauncher
  (`tools/VisualRelay.Relauncher/Program.cs:40-55`) reads
  `handoff.RelaunchCommand` as the command to restart THE APP. Executed as
  recorded, the relauncher respawns itself, not the app; the actual app
  restart command is recorded nowhere.
- **Bug 2 — the argv shape is wrong even for what it records.** The two
  elements are `FileName` plus ONE pre-joined argument string, but
  `Program.cs:66-70` feeds `cmd.Skip(1)` into `ProcessStartInfo.ArgumentList`,
  which escapes each element as a single argv token. `dotnet` therefore
  received the entire `run --project … -- --parent-pid …` line as one
  argument, failed immediately, and the relauncher exited 0.
- **Bug 3 — total silence on failure.** The relauncher writes nothing to the
  drain log, does not check whether the spawned process survived or the new
  instance came up, and its stderr goes to inherited stdio that dies with
  the chain. Task 00's constraint — "a failed relaunch … must leave the
  sidecar plus a loud drain-log event for diagnosis" — is unmet: the sidecar
  is left only by accident, and no event is written.
- Knock-on: `RestartHandoff.IsStale` (`RestartHandoff.cs:98-100`) discards
  handoffs older than 5 minutes, so by the time a human notices the strand,
  a plain relaunch silently drops the continuation too.

### What to build

1. **Record the app-restart command, separately and correctly.** The handoff
   must carry a true argv array for restarting THE APP "the same way it was
   started" (source checkout → the recompiling path, e.g. the bootstrap
   script or `dotnet run --project src/VisualRelay.App`; published install →
   the published binary), one argument per element, never a pre-joined
   command line. How the relauncher itself is spawned is a separate concern
   and must not be conflated with this field.
2. **Spawn correctly.** The relauncher passes each argv element through as
   one `ArgumentList` entry. It must never target `VisualRelay.Relauncher`
   as the thing to restart.
3. **Verify and report.** After spawning, the relauncher confirms the new
   instance actually arrived (the sidecar gets consumed, or the control
   port answers, within a bounded wait) before exiting success. On any
   failure — spawn throws, child dies, no arrival — it writes a loud
   drain-log event (it already references Core; `DrainSummaryLog` is
   available) and leaves the sidecar in place for diagnosis.
4. **Keep the existing guards intact:** bind-conflict remains fail-loud (the
   relauncher still waits for the parent pid), the no-progress guard and
   5-minute staleness window stay, and a user pause still wins over
   auto-continue.

### Constraints

- Repo-agnostic: nothing may assume the target repo is Visual Relay; paths
  come from the handoff/environment, not hardcoded checkout layout.
- No shell string-joining anywhere in the chain — argv arrays end to end.
- Keep files under the 300-line guard; use TimeProvider patterns for any
  waits in tests (no real-time sleeps).

### Tests (red first)

- Handoff round-trip: the recorded `RelaunchCommand` targets the app (not
  `VisualRelay.Relauncher`) and is a real argv array — no element contains
  a multi-token command line (fails today on both counts).
- Relauncher spawn test against a fake app (a script that records its argv
  and writes a marker): relauncher started with a handoff pointing at the
  fake app spawns it with exactly the recorded argv, waits for arrival, and
  exits 0 (fails today: it would respawn itself with a mangled argv).
- Failure path: handoff whose argv points at a nonexistent binary → the
  relauncher exits nonzero, the sidecar remains, and a drain-log event
  describing the failure exists (fails today: silent exit 0).

### Verification

- `./visual-relay check` fully green including the new tests.
- Manual self-hosted smoke: two trivial queued tasks under Restart Between
  Tasks; confirm via drain log that the boundary produces `restart-handoff`
  followed by a `restart-resume` from a NEW pid/version with no human help.
