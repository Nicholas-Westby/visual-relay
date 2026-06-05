# Graceful Config Loading & Guided Initialization

- **Date:** 2026-06-04
- **Status:** Approved (design) — pending implementation plan
- **Audience:** Both (a) people running Visual Relay against their own projects and (b) contributors who clone this repo and run its bundled `llm-tasks/`.

## Problem

Selecting any folder without a hand-crafted `.relay/config.json` yields an empty
queue and a truncated, dead-end error in the status bar. We reproduced two
failures back-to-back against this very repo:

1. **Missing `.relay/config.json`** → `RelayConfigLoader.LoadAsync` throws
   `FileNotFoundException`, even though `Defaults()` already knows
   `TasksDir = "llm-tasks"`.
2. **Omitted `logSources`** → `ReadStringArray` throws `relay config: logSources
   must be an array`, even though the field has a sensible default of `[]`.

### Root cause

`RelayConfigLoader.LoadAsync` is the single, throwing entry point for **both**
listing tasks and running them (`RelayTaskRepository.ListAsync` line 31;
`MainWindowViewModel.Execution.cs` line 74). A non-fatal *setup* gap is therefore
escalated into a hard failure on the *read* path, and the only feedback is
`RunBusyAsync`'s catch writing the exception message to a single, trimmed status
line. The user is given a problem with no path forward.

## Goals

- Browsing/listing a project never fails because of config state.
- A folder with no usable config leads the user to a working config in one
  guided action — not a prompt telling them to go write JSON.
- Running never silently proceeds with a bogus test command.
- This repo works out of the box on clone.
- Genuinely malformed configs produce a full, actionable error (not a truncated
  string).

## Non-goals

- No change to the Relay stage model, pricing, or Swival execution.
- No multi-field config editor UI — initialization captures the one field that
  matters (`testCmd`); everything else uses defaults.
- No new provider/model wiring — LLM-assisted detection reuses the existing
  `frontier` tier through the local proxy.

## Design

### 1. Forgiving core (the bug-class fix)

**Non-throwing load with status.** Add a load that never throws and returns a
result:

```
RelayConfigResult(RelayConfig Config, RelayConfigStatus Status, string? Diagnostic)
RelayConfigStatus = Loaded | Defaulted | Incomplete | Malformed
```

- `Loaded` — valid file with required fields.
- `Defaulted` — no file; `Config` is `Defaults()` (so `TasksDir = "llm-tasks"`).
- `Incomplete` — file present but required `testCmd` missing/blank.
- `Malformed` — file present but invalid JSON or a field present with the wrong
  type; `Diagnostic` carries the full message.

**Listing uses this and never throws.** `RelayTaskRepository.ListAsync` and
`ListCompletedAsync` switch to the non-throwing load. For `Defaulted` /
`Incomplete` they still enumerate `llm-tasks/` and return the real task list. For
`Malformed` they return no tasks but pass the diagnostic up for display.

**Truly-optional fields.** `logSources` (and any peer with a default) returns its
default when absent; a value is only rejected when *present but the wrong type*.
Only `testCmd` remains required, and it is enforced solely at run time.

**Run-time guard.** The run path inspects `Status`. `Loaded` runs as today.
`Defaulted` / `Incomplete` route into guided initialization (Section 2) instead
of throwing or running with a placeholder command. `Malformed` surfaces the
diagnostic (Section 3) — initialization will not silently overwrite a file the
user wrote.

**Compatibility.** `LoadAsync` is retained as a thin wrapper that throws on any
non-`Loaded` status, for callers that legitimately require a complete config; the
run path is migrated to inspect status directly so it can route to init.

### 2. Guided initialization (shared by GUI and CLI)

A single initialization capability with three layers: **detect → present →
write**.

**Detection (`TestCommandDetector`).** Infer `testCmd` from project markers:

| Marker | Detected `testCmd` |
| --- | --- |
| `*.slnx`, `*.sln`, `*.csproj` | `dotnet test` |
| `pyproject.toml` / `setup.py` / `tests/` | `pytest` |
| `package.json` | `npm test` |
| `Cargo.toml` | `cargo test` |
| `go.mod` | `go test ./...` |
| none of the above | *(empty — hand off to manual entry or LLM, Section 2c)* |

Each marker maps to one deterministic default; the user always sees and can edit
it before writing. Finer command selection (e.g. `bun test` vs `npm test`,
`pytest` vs `unittest`) is a refinement for the implementation plan, not a
spec-level decision — the confirm-before-write step makes a wrong guess cheap.

**GUI empty-state.** When the selected folder's status is `Defaulted` /
`Incomplete`, the queue area shows an **Initialize this project** panel:

- Pre-filled with the detected `testCmd` in a single editable text box.
- A **Create config** button writes `.relay/config.json`, then auto-refreshes so
  the queue populates. No blank page, no raw-JSON editing, no docs.

**GUI run-attempt.** Clicking **Run** on a `Defaulted` / `Incomplete` folder does
not refuse — it routes into the same panel ("Visual Relay can set this up —
detected `dotnet test`"). After the user confirms, it writes the config **and
proceeds to run the task they asked for**. Run never silently fails and never
dead-ends.

**2c. Unknown project (detection returns empty).** The panel still helps:

- **Manual entry with examples** — the text box shows representative commands as
  placeholder/hint text (`dotnet test`, `pytest`, `bun test`, `cargo test`,
  `go test ./...`).
- **"Find it for me" (LLM)** — a button that tasks the **frontier** tier with
  discovering the command. It issues a single non-streaming chat completion to
  the proxy (`ModelBackend.BaseUrl` + OpenAI-compatible `/chat/completions`,
  `model: "frontier"`) with project context (top-level file listing plus any
  manifest/README excerpts, bounded in size). The returned command pre-fills the
  text box for the user to confirm before writing. This path is gated on
  `BackendReadinessProbe`; if the backend is down, the button is disabled with a
  hint pointing at the existing Start-backend affordance, and manual entry
  remains available.

**CLI (`./visual-relay init [path]`).** Headless equivalent for scripted/remote
setup: detect, write `.relay/config.json`, report the result and chosen command.
Defaults `path` to the current directory. (LLM-assisted detection is a GUI
affordance; the CLI writes the detected command, or — when detection fails —
writes `"testCmd": ""` (which loads as `Incomplete`, so the GUI guides the user
next time) and prints clear guidance to stdout. `.relay/config.json` is plain
JSON, so no in-file comment is written.)

### 3. Error clarity for malformed configs

A config that *exists but is broken* (invalid JSON, wrong field types) is not an
init problem. The `Malformed` status itself is produced by the loader in Phase 1,
where its full `Diagnostic` is shown via the existing status text (already
better than today's silent overwrite, since init never touches a malformed file).
Phase 3 upgrades that to a **full**, non-truncated, dismissible banner with a
resolution hint, reusing the existing `error-message-resolution-hints` pattern.

### 4. Ship this repo's config

- Commit a valid `.relay/config.json` at the repo root (testCmd =
  `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1
  -p:UseSharedCompilation=false`).
- Add to `.gitignore`:
  ```
  .relay/*
  !.relay/config.json
  ```
  so per-task runtime artifacts (`<taskId>/`, `ACTIVE`, halt marker, `run.log`,
  ledgers, seals) stay out of git while the config is tracked.

## Components & seams

| Unit | Responsibility | Depends on |
| --- | --- | --- |
| `RelayConfigResult` / `RelayConfigStatus` | Carry config + load status + diagnostic | Domain |
| `RelayConfigLoader.TryLoadAsync` | Non-throwing load; classify status | JSON, `Defaults()` |
| `RelayTaskRepository` (list paths) | List tasks regardless of config state | `TryLoadAsync` |
| Run path (`RunOneAsync`) | Gate on status; route to init or surface error | `TryLoadAsync` |
| `TestCommandDetector` | Marker-based `testCmd` inference | filesystem |
| `LlmTestCommandFinder` | Frontier one-shot command discovery | proxy (`ModelBackend`), `BackendReadinessProbe` |
| `RelayConfigWriter` | Write a minimal valid `.relay/config.json` | filesystem |
| GUI init panel + empty-state | Detect → present → write/run flow | detector, finder, writer |
| `./visual-relay init` | Headless detect + write | detector, writer |

## Data flow (no-config → running)

1. User selects folder → `TryLoadAsync` → `Defaulted`.
2. List path still enumerates `llm-tasks/` → queue populates; empty-state panel
   shows detected `testCmd`.
3. User clicks **Create config** (or **Run**) → `RelayConfigWriter` writes
   `.relay/config.json` → refresh → status now `Loaded` → (if Run) task executes.
4. If detection was empty → user types a command or clicks **Find it for me**
   (frontier) → confirm → write → continue.

## Error handling

- Missing config → `Defaulted`, never an error; guided init.
- File present, no `testCmd` → `Incomplete`; guided init (does not overwrite
  without confirmation).
- Invalid JSON / wrong types → `Malformed`; full diagnostic + hint banner; no
  auto-overwrite.
- Backend down during LLM detection → button disabled + hint; manual entry
  unaffected.

## Testing

- Loader: missing file → `Defaulted` and tasks still listed; omitted `logSources`
  → default `[]`; present-but-wrong-type → `Malformed` with message; missing
  `testCmd` → `Incomplete`.
- Run guard: `Defaulted`/`Incomplete` routes to init rather than throwing.
- Detector: correct `testCmd` per marker; unknown → empty.
- Writer: output re-loads as `Loaded`.
- `LlmTestCommandFinder`: prompt assembly + response parsing against a mocked
  proxy client; backend-down path disables cleanly.
- Existing discovery/config/archive tests stay green.

## Phasing

1. **Core:** forgiving loader + status, optional fields, run-time guard, ship
   repo config + `.gitignore`. (Removes the bug class for both audiences.)
2. **Guided init:** detector, writer, GUI empty-state/panel, CLI `init`.
3. **Unknown-project help:** manual entry with examples + frontier
   `LlmTestCommandFinder`, plus malformed-config error banner.
