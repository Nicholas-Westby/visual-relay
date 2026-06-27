# No durable run log: a failed run leaves nothing human-readable to tail

Visual Relay emits structured `RelayEvent`s for everything it does (stage starts, traces, stage
reports, flags), but nothing persists them. The only `IRelayEventSink` implementations are
`NullRelayEventSink` (no-op) and `ObservableRelayEventSink`
(`src/VisualRelay.App/Services/ObservableRelayEventSink.cs`), which posts events to the in-memory
UI panes via `Dispatcher.UIThread` and keeps no file. When the app closes, switches tasks, or
the user is driving it remotely, the run narrative is gone.

The only durable per-run artifacts are the Swival `report.json` (structured, per-stage, not a
tailable narrative) and the `NEEDS-REVIEW` marker (one terse line). So answering "what happened
on that run, and why did it fail?" means hand-reading JSON — the gap that made the stage-1
backend outage hard to diagnose.

Note: the target project's `logs/app.log` is **not** the place for this — it's a task *input*
log source (`.relay/config.json` → `logSources: ["logs/app.log"]`, read by the LLM stages).
Writing Visual Relay diagnostics there would corrupt task input. This task adds Visual Relay's
own run log.

## Recommended fix

Persist the existing event stream to a durable, tailable run log — reuse `RelayEvent`, just add
a consumer:

1. **Add a file-backed sink** (e.g. `FileRelayEventSink : IRelayEventSink`) that appends one
   human-readable line per event (timestamp, run id, `s{stage}/{tier}`, event name, key data).
   Append-only so it can be `tail -f`'d during a run.
2. **Fan out to it** alongside the UI sink. The driver takes a single `IRelayEventSink`
   (`RelayDriverDependencies`), and the app wires one sink into both runner and driver
   (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs:180-182`). Add a small
   composite sink that forwards each event to `[observable, file]` so both UI and file see every
   event without changing the driver contract.
3. **Log the run header.** At run start, record the resolved model backend (the `base_url` from
   `SwivalProfileSession` and the `tier → model` mapping) and, on failure, the full error
   message — the facts needed to diagnose a run from the log alone. (Today no event carries the
   resolved `base_url`; add it to the run-start event.)
4. **Write to a Visual Relay-owned path**, e.g. `.relay/<task>/run.log` (per task) and/or a
   global app-data log — never the target project's `logSources`. Add a `.gitignore` rule for it
   (`.relay/<task>/` already holds committed proof files like `ledger.md`/`*.seals`, so the log
   must be explicitly ignored).

## Sequencing

- Independent, but if `preflight-model-backend-readiness.md` lands first, reuse its centralized
  `base_url`/port source for the run header instead of re-reading it from the profile generator.

## Done when

- After a run (success or failure), a durable `run.log` exists with a readable, ordered record:
  run header (backend `base_url`, tier→model), each stage start/report, and the final outcome
  including the full error message on failure.
- The log is append-only and tailable during a run; the UI panes still update exactly as today.
- The target project's `logs/app.log` (and any configured `logSources`) is never written to by
  Visual Relay.
- The run log path is git-ignored.
- Unit coverage: the file sink writes the expected lines for a sequence of events; the composite
  sink forwards to all children. Write the failing tests first.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
