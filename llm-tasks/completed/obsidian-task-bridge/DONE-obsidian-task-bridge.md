# Obsidian task bridge: create tasks and publish run summaries through an iCloud Obsidian vault

Add an **Obsidian bridge** so a person can create Visual Relay tasks and watch their results from
another device (e.g. a phone) through an iCloud-synced Obsidian vault — a simple, file-based "remote
control." It works in two directions:

- **Ingress (create):** markdown files dropped into a `New Tasks/` vault folder are detected while the
  app is idle, turned into real `llm-tasks/<slug>/<slug>.md` tasks, stamped as recognized, moved aside,
  and (unless paused) run automatically.
- **Egress (monitor):** every task the app finishes (committed or flagged) gets a human-readable
  summary written to `Completed/<yyyy-MM-dd>/<id>.md` in the vault, so progress is visible remotely.

This is **purely additive** — it does not replace or change the existing `llm-tasks/` foundation. The
vault is an alternate front door and a read-only mirror; the repo's `llm-tasks/` tree stays the single
source of truth, and an imported task runs through the normal Relay pipeline unchanged. This design is
decided — implement exactly this, no alternatives.

## Current state (researched)

> **Freshness contract.** Verify every reference below by searching for the quoted symbol/string, not
> by line number; re-read the file if a snippet has drifted.

**Task files: nested subfolders, scanned and skipped by name.** `RelayTaskRepository.Walk`
(`src/VisualRelay.Core/Tasks/RelayTaskRepository.cs`) emits one task per subfolder; the canonical
markdown is `llm-tasks/<id>/<id>.md`. `private static readonly HashSet<string> SkippedDirectories =
["completed", "_ideation"]` and `IsSkippedName` (`DONE-`/`IGNORE-` prefixes) define what is ignored.
Pending order is alphabetical by `Id` (`OrderBy(task => task.Id, …)` in `ListAsync`), so any imported
slug sorts in naturally.

**Task creation is a pure static writer.** `RelayTaskWriter` (`src/VisualRelay.Core/Tasks/RelayTaskWriter.cs`):
- `Slugify(title)` → filesystem-safe kebab-case.
- `ValidateSlug(slug, rootPath)` → null when OK; rejects empty/unsafe/reserved-prefix (`DONE-`,
  `IGNORE-`) slugs and **collisions** (`A task named "<slug>" already exists`).
- `CreateAsync(rootPath, slug, markdown)` → writes `llm-tasks/<slug>/<slug>.md` (creates dirs),
  returns the path. **Reuse these verbatim for ingress — do not reinvent slugging or collision rules.**

**Per-machine config already lives under XDG, with a test seam.** `KeyEnvFile`
(`src/VisualRelay.Core/Configuration/KeyEnvFile.cs`) resolves `$XDG_CONFIG_HOME/visual-relay/…`
(falling back to `$HOME/.config/visual-relay/…`) via `internal static string ResolvePath(string?
xdgConfigHome, string? home)` and reads env through the `IEnvironmentAccessor` seam (`GetEnv`,
`accessor?.GetEnvironmentVariable(...) ?? Environment.GetEnvironmentVariable(...)`). It creates the
config dir `0700` and files `0600`. **Mirror this exact pattern** for the bridge's own settings file.
This matters because the repo is shared with a VM (the host and VM mount the same `llm-tasks/`), so a
host-only iCloud path must NOT go in the in-repo `.relay/config.json` — it belongs in per-machine XDG.

**Run summaries are a pure function of on-disk artifacts.** Under `.relay/<id>/`:
- `RelayRunHistory.ReadTaskMetric(rootPath, taskId)` (`src/VisualRelay.Core/Tasks/RelayRunHistory.cs`)
  → `TaskRunMetric(TaskId, Stages)` where each `StageRunMetric` (`src/VisualRelay.Domain/RunMetrics.cs`)
  carries `StageNumber, StageName, Tier, Model, Timestamp, DurationSeconds, CostUsd, Turns`, plus
  `CostLabel`/`DurationLabel`. `TaskRunMetric` exposes `CostUsd`, `DurationSeconds`,
  `CompletedStageCount`, `SummaryLabel`.
- `RelayRunHistory.ReadStatusRecord(rootPath, taskId)` → the per-stage `StageStatusEntry` list (status
  `Done`/`Flagged`, and `check` `red`/`green`). This is the per-stage table source.
- **Completion time = the max `Timestamp` across the task's stages.** Per project knowledge, the
  authoritative timestamp is the `"timestamp"` field *inside* the report JSON (already parsed into
  `StageRunMetric.Timestamp`); file mtimes are unreliable here because VM syncs reset them. Use a
  fallback chain (max stage `Timestamp` → newest `.relay/<id>` mtime → now) and never sort by mtime.

**Terminal completion has exactly two hook sites — both already exist.**
- Single run: `RunOneAsync` (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs`) awaits
  `driver.RunTaskAsync(...)` → `outcome`, then `LoadRunHistoryAsync`. `RelayTaskOutcome`
  (`src/VisualRelay.Domain/RelayTaskOutcome.cs`) is `(TaskId, Status, TaskHash, CommitSha, Reason)`
  with `RelayTaskOutcomeStatus { Committed, Flagged, Failed, Planned }`.
- Drain: `CreateDrainLifecycleCallbacks()` (`src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs`)
  defines `OnExecuteCompleted = (taskId, _) => { ClearRunningTask(taskId); … ReconcileArchivedTaskRow(taskId); }`.
  This is the per-task drain-completion hook.

**Background timers follow one pattern; start only from App startup.** `MainWindowViewModel.cs` owns
`DispatcherTimer? _backendMonitor` (15 s, `StartBackendMonitoring()`) and `DispatcherTimer?
_elapsedTimer` (1 s, `StartElapsedTimer()`). Both carry the comment *"Called ONLY from App startup
(never the ctor or LoadInitialAsync) so unit tests spin no timer"* and are invoked from `App.axaml.cs`.
Tests drive the work directly (e.g. `UpdateRunningElapsedLabels()`), never the timer. **Add the bridge
timer the same way.**

**Idle / busy signals.** `IsBusy` (`_isBusy`) is true for the whole duration of any run/drain;
`_runningTaskIds` (HashSet) tracks concurrent runs; `PauseRequested` is the user's "don't start new
work" lever; `IsSettingsOpen`, `IsEditingMarkdown`, `IsNewTaskDialogOpen` mark interactive UI states.
The drain entry point is `DrainQueueAsync` `[RelayCommand(CanExecute = nameof(CanDrain))]`; it routes
through `EnsureRunnableAsync(null)` (config/keys/tools gate) before doing anything.

**Repo / vault naming.** `RootFolderDisplay.Name(rootPath)` (`src/VisualRelay.App/ViewModels/RootFolderDisplay.cs`)
returns the repo's folder name; the VM exposes it as `RootName` (`MainWindowViewModel.Properties.cs`,
`RootName => RootFolderDisplay.Name(RootPath)`). Use it for the per-repo vault subfolder. The folder
picker is `_folderPicker.PickFolderAsync()` (`IFolderPicker`, used in `MainWindowViewModel.Commands.cs`).

**Settings UI + control API are the established surfaces.** `SettingsPanel.axaml`
(`src/VisualRelay.App/Views/Controls/`) hosts toggles like `CommitProofCheckBox` bound to VM
properties that persist on change (see `MainWindowViewModel.Settings.cs`
`OnCommitProofArtifactsChanged`). The loopback control API (`ControlApi.cs` `ResolveCommand` switch +
`ControlApi.State.cs` `BuildCommandsMap`) registers commands like `run-all`, `select-task`,
`bypass-sandbox` (with a JSON body) — the headless way the implementer drives/screenshots features
(`AGENTS.md` "Driving the running app").

**Sandbox note (no change needed).** The bridge's file I/O runs **in the app process**, not inside a
`nono`/`vr-guard` swival run, so the sandbox writable-set does not constrain it and needs no edits.
Writing under `~/Library/Mobile Documents/…` is therefore fine. Only the LLM stages are sandboxed.

## Vault layout (decided)

Default vault root (macOS/iCloud Obsidian), `~` expanded from `$HOME`:

```
~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/
```

Per-repo subfolder = `RootName` (e.g. repo at `~/Dev/Stuff` → `…/Visual Relay LLM Tasks/Stuff/`).
Inside each per-repo folder:

```
<RootName>/
  INFO.md               ← seeded guide: what this folder is and how the bridge works
  New Tasks/            ← user drops *.md here (from any device); the scan watches this
    INFO.md             ← seeded guide: how to create a task by dropping a file
    Recognized/         ← imported source files move here, stamped with vr-recognized frontmatter
      INFO.md           ← seeded guide: these are the originals after recognition
  Completed/            ← run summaries, organized by date
    INFO.md             ← seeded guide: auto-generated read-only summaries
    <yyyy-MM-dd>/<id>.md   ← one generated run summary per completed task, dated by completion
```

**The app creates every folder automatically, and seeds an `INFO.md` into each main folder.** Empty
folders are not reliably synced or shown by iCloud/Obsidian, so each main folder is given an `INFO.md`
both to make the folder appear on every device and to document the system for the person using it.
`INFO.md` files are **never** treated as tasks (see import exclusion below). The whole tree is created
lazily on the first enabled scan; nothing is written unless the feature is enabled. Seeding is
idempotent and **never overwrites** an `INFO.md` the user has edited — it only writes one when absent.

## What to build

TDD — write the failing tests first. Keep all logic in pure, injectable Core classes that test against
a **temp directory as the vault root** and a temp repo (no real iCloud, no GUI, no swival). Headless UI
assertions use `[AvaloniaFact]` (per `AGENTS.md`); the VM timer is tested by calling its cycle method
directly, never by spinning the timer. Put new Core code under `src/VisualRelay.Core/ObsidianBridge/`.

1. **`ObsidianBridgeSettings`** (`src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs`, static,
   mirroring `KeyEnvFile`): per-machine settings JSON at `$XDG_CONFIG_HOME/visual-relay/obsidian.json`
   (fallback `$HOME/.config/visual-relay/obsidian.json`), resolved through the same
   `IEnvironmentAccessor` seam and the same `internal static string ResolvePath(string? xdgConfigHome,
   string? home)` shape so it is unit-testable with an injected temp HOME. Shape:
   ```json
   { "enabled": false, "vaultRoot": "<default iCloud path>", "pollSeconds": 60 }
   ```
   `Load(accessor?)` returns a record with defaults applied (missing file → defaults; `enabled` defaults
   **false**; `vaultRoot` defaults to the iCloud path above with `~`/`$HOME` expanded; `pollSeconds`
   defaults 60, clamped to a sane floor e.g. ≥ 15). `Save(settings, accessor?)` writes the file (create
   dir `0700`, file `0600`, like `KeyEnvFile.Upsert`). Tests: round-trips; missing file → defaults;
   `enabled` is off by default; non-macOS / unset `$HOME` degrades to disabled rather than throwing.

2. **`ObsidianVaultLayout`** (`src/VisualRelay.Core/ObsidianBridge/ObsidianVaultLayout.cs`, pure): given
   `vaultRoot` + `repoName`, computes `RepoDir`, `NewTasksDir`, `RecognizedDir`, `CompletedDir(DateOnly)`,
   and `SummaryPath(taskId, DateOnly)`. Sanitizes `repoName` for the filesystem (strip path separators;
   non-empty fallback). `EnsureScaffold()` creates `RepoDir`, `New Tasks/`, `New Tasks/Recognized/`, and
   `Completed/`, then **seeds an `INFO.md` into each of those folders when one is absent** (idempotent;
   never overwrites a user-edited `INFO.md`) using the templates in the "INFO.md guides" section below.
   Expose the reserved non-task filename(s) (`INFO.md`, plus `README.md`) as a public set the importer
   reuses. Tests: path composition; date folder name is `yyyy-MM-dd`; sanitization; `EnsureScaffold`
   creates the folders and writes the four `INFO.md` files; a second call is a no-op and does not clobber
   an `INFO.md` whose contents were changed.

3. **`ObsidianTaskImporter`** (`src/VisualRelay.Core/ObsidianBridge/ObsidianTaskImporter.cs`):
   `IReadOnlyList<ImportCandidate> Scan(layout, nowUtc, minStableAge)` enumerates `NewTasksDir/*.md`
   (top level only; never `Recognized/`) and **excludes**: the reserved seeded files (`INFO.md`,
   `README.md`, case-insensitive — reuse the set from `ObsidianVaultLayout`) so a guide file is never
   imported as a task; files already carrying `vr-recognized:` frontmatter; iCloud not-yet-downloaded
   placeholders (`.icloud` / zero-length sentinels); and files whose last write is newer than
   `minStableAge` (default ~10 s) so a half-synced file isn't imported mid-write ("debounce to wait for
   the file to settle"). `Recognize(candidate, rootPath, nowUtc, newGuid)`:
   - Derive the title: explicit `title:` frontmatter → first `# H1` → filename without extension.
   - `slug = RelayTaskWriter.Slugify(title)`; resolve collisions by suffixing `-2`, `-3`, … using
     `RelayTaskWriter.ValidateSlug(slug, rootPath)`; if no free slug after a small bound, **skip and
     report** (leave the file untouched in `New Tasks/`, do not stamp) so it isn't lost.
   - Strip any leading YAML frontmatter from the body, then `RelayTaskWriter.CreateAsync(rootPath, slug,
     body)`.
   - Stamp the **source** file's frontmatter with `vr-task-id: <slug>`, `vr-recognized: <newGuid>`,
     `vr-recognized-at: <ISO-8601 UTC>`, `vr-repo: <repoName>` (preserve the original body byte-for-byte
     below the frontmatter), then **move** it into `RecognizedDir` (create on demand; on name clash add a
     numeric suffix). Two independent idempotency layers: the `vr-recognized` stamp and the move out of the
     scan folder.
   - Return an `ImportResult(slug, sourceGuid, recognizedPath)` (or a skip reason).
   `newGuid` and `nowUtc` are **injected** (no `Guid.NewGuid()`/`DateTimeOffset.UtcNow` inside the pure
   method) so tests are deterministic. Tests (temp vault + temp repo): a fresh file becomes
   `llm-tasks/<slug>/<slug>.md` with clean body; the source is stamped and moved to `Recognized/`; a second
   `Scan` returns nothing for it (both because it moved and because it is stamped); a file < `minStableAge`
   is skipped; an `.icloud` placeholder is skipped; a colliding title gets a suffixed slug; an
   unresolvable collision is reported and the file is left in place unstamped; frontmatter stripping
   keeps the intended task body.

4. **`ObsidianSummaryWriter`** (`src/VisualRelay.Core/ObsidianBridge/ObsidianSummaryWriter.cs`, pure):
   `string Build(rootPath, taskId, RelayTaskOutcome?, specMarkdown, sourceGuid?, nowUtc)` renders the
   summary markdown (template below) from `RelayRunHistory.ReadTaskMetric` + `ReadStatusRecord` + the
   outcome. `Write(layout, rootPath, taskId, outcome?, specMarkdown, sourceGuid?, nowUtc)` resolves the
   completion date (max stage `Timestamp` → fallback chain, never mtime), writes
   `CompletedDir(date)/<id>.md` (create dirs), and **overwrites** an existing summary (re-export
   refreshes it). Map status → label: `Committed`→`committed`, `Flagged`→`needs-review`,
   `Failed`→`failed`; when `outcome` is null (reconcile pass with no in-memory outcome) infer from the
   status record / `NEEDS-REVIEW` marker. Tests: a committed metric renders the commit sha, cost,
   duration, the per-stage table, and embeds the spec; a flagged task renders `needs-review` and the
   reason; date folder matches the max stage timestamp; re-`Write` overwrites in place.

5. **VM partial `MainWindowViewModel.ObsidianBridge.cs`** (+ backing fields on `MainWindowViewModel.cs`):
   - Load settings once at startup into VM state: `_obsidianEnabled`, `_obsidianVaultRoot`,
     `_obsidianPollSeconds` (persist changes via `ObsidianBridgeSettings.Save`, mirroring
     `OnCommitProofArtifactsChanged`). Expose `[ObservableProperty]` bindings + a `BrowseVaultRootAsync`
     command using `_folderPicker.PickFolderAsync()`.
   - **`internal async Task RunObsidianBridgeScanAsync()`** — the testable core cycle, **no drain**:
     when `_obsidianEnabled` and `RootPath` is valid, build the layout from `_obsidianVaultRoot` +
     `RootName`, `EnsureScaffold()`, run `ObsidianTaskImporter.Scan`/`Recognize` for each candidate
     (injecting `Guid.NewGuid()`/`DateTimeOffset.UtcNow` here at the app boundary), then run an **export
     reconcile**: for completed tasks (`ListCompletedAsync`, bounded to a reasonable recent count)
     missing a `Completed/<date>/<id>.md`, write the summary. Returns the count imported. Everything is
     best-effort: catch and surface via `StatusText`/run log; a vault error must never break a run.
     Marshal VM mutations (`ReloadTaskListAsync`, status, command `NotifyCanExecuteChanged`) to the UI
     thread, reusing the drain's dispatch discipline.
   - **`StartObsidianBridge()`** — a `DispatcherTimer` at `_obsidianPollSeconds`, **called only from
     `App.axaml.cs` startup** (with the same "never ctor/tests" comment as `StartBackendMonitoring`).
     Tick handler: skip if not enabled, if not idle (`IsBusy || _runningTaskIds.Count > 0 ||
     IsSettingsOpen || IsEditingMarkdown || IsNewTaskDialogOpen`), or if a previous cycle is still
     running (a private `_bridgeCycleBusy` reentrancy guard — debounce). Otherwise `await
     RunObsidianBridgeScanAsync()`; then **auto-run**: if it imported ≥ 1 task, the queue has pending
     work, and `!PauseRequested`, invoke the existing drain (`DrainQueueCommand.ExecuteAsync(null)` —
     it self-gates via `CanDrain`/`EnsureRunnableAsync`, runs serially "one at a time", and handles
     planning/needs-review/circuit-breaker). Pausing the queue suppresses only the auto-run; scan,
     import-stamp, and export still happen (an "import but hold" mode).
   - **Export on completion (live):** call `ObsidianSummaryWriter.Write(...)` at both terminal hooks —
     in `RunOneAsync` after `outcome` is known, and inside `CreateDrainLifecycleCallbacks`'
     `OnExecuteCompleted` — guarded by `_obsidianEnabled`, best-effort. This makes summaries appear
     promptly without waiting for the next reconcile tick. (The reconcile pass in step 5 is the safety
     net for missed events and pre-existing completions.) Read the spec markdown to embed from the
     task's current/`DONE-`/archived path (it may have already moved to `completed/` by retirement —
     resolve via `RelayTaskRepository` rather than assuming `llm-tasks/<id>/<id>.md`).

6. **Settings UI** — add an **Obsidian bridge** section to `SettingsPanel.axaml`: an enable checkbox,
   a vault-root `TextBox` + **Browse** button (the `BrowseVaultRootAsync` command), and a poll-interval
   field, with a one-line explanation that files in `New Tasks/` become tasks and summaries publish to
   `Completed/`. Keep the file **under 300 lines** (`tools/guards/check-file-size.sh`); if it would
   exceed, extract the section into a small `Views/Controls/ObsidianSettings.axaml` child control rather
   than inlining.

7. **Control API parity** (`ControlApi.cs` `ResolveCommand` + `ControlApi.State.cs` `BuildCommandsMap`):
   register `obsidian-scan` → a command that runs one `RunObsidianBridgeScanAsync()` cycle now, and
   `obsidian-bridge` (JSON body `{"value":true|false}` to toggle enabled, `{"path":"…"}` to set the
   vault root) following the `bypass-sandbox` body pattern. This lets the implementer drive the feature
   headlessly (point at a temp vault, drop a file, POST `obsidian-scan`, assert the task and summary
   appear) per `AGENTS.md`.

## Summary file template (lift into `ObsidianSummaryWriter`)

```markdown
---
vr-task-id: <id>
vr-status: committed | needs-review | failed
vr-repo: <RootName>
vr-completed-at: <ISO-8601 UTC>
vr-commit: <sha or empty>
vr-cost-usd: <number>
vr-duration: <e.g. 12m 03s>
vr-source-guid: <guid if imported from Obsidian, else empty>
---

# <id>

**Status:** Committed · **Commit:** `abc1234` · **Cost:** $0.21 · **Duration:** 12m 03s · **Completed:** 2026-06-20 14:30 UTC

> <one-line outcome: commit subject, or the flag/needs-review reason>

## Stages

| # | Stage | Status | Check | Model | Turns | Time | Cost |
|---|-------|--------|-------|-------|-------|------|------|
| 1 | Ideate | Done | – | cheap-kimi | 7 | 26s | $0.004 |
| … | … | … | … | … | … | … | … |

## Task

<the task spec markdown, embedded verbatim so the summary is self-contained in Obsidian>
```

## Recognized-source frontmatter stamp (lift into `ObsidianTaskImporter`)

```markdown
---
vr-task-id: <slug>
vr-recognized: <guid>
vr-recognized-at: <ISO-8601 UTC>
vr-repo: <RootName>
---
<original file content, preserved>
```

## INFO.md guides (seed these into the folders; lift the copy into `ObsidianVaultLayout`)

Plain, friendly, device-agnostic. Keep them short. Suggested content:

- **`<RootName>/INFO.md`**
  ```markdown
  # Visual Relay — <RootName>

  This folder is a remote control for the Visual Relay project **<RootName>**, synced via iCloud so
  you can use it from any device (including your phone) in Obsidian.

  - **Create a task:** add a markdown file in **New Tasks/**. See that folder's INFO.md.
  - **Watch results:** completed runs appear as summaries in **Completed/**, organized by date.

  Visual Relay manages these folders automatically. You only ever add files to **New Tasks/**.
  ```

- **`<RootName>/New Tasks/INFO.md`**
  ```markdown
  # New Tasks

  Drop a markdown file here to ask Visual Relay to do something. The first `# Heading` (or the file
  name) becomes the task title; the rest is the task description.

  When the app is running and idle it will pick the file up, create the task, and (unless the queue is
  paused) start working on it. Your original file then moves to **Recognized/**, stamped so it is only
  ever taken once. Give a file a few seconds to finish syncing before expecting it to be picked up.

  (This INFO.md is ignored — it never becomes a task.)
  ```

- **`<RootName>/New Tasks/Recognized/INFO.md`**
  ```markdown
  # Recognized

  These are the original request files after Visual Relay turned them into tasks. Each is stamped with a
  `vr-recognized` id in its frontmatter. They are kept for your reference — safe to leave or delete.
  ```

- **`<RootName>/Completed/INFO.md`**
  ```markdown
  # Completed

  Auto-generated, read-only summaries of finished runs, in dated subfolders (`YYYY-MM-DD`). Each shows
  the outcome, cost, duration, per-stage results, and the task itself. Editing files here has no effect.
  ```

## Decisions (settled)

1. **Additive, never a replacement.** The vault is an alternate ingress + a mirror; `llm-tasks/` stays
   the source of truth and the pipeline is unchanged. *Why:* the user explicitly framed this as "an
   added layer of convenience," not a new task store.
2. **Per-machine settings in XDG (`visual-relay/obsidian.json`), not `.relay/config.json`.** *Why:* the
   repo is shared with a VM; the iCloud vault path is host-specific and must not leak into in-repo,
   committed config. Mirror `KeyEnvFile`'s XDG + `IEnvironmentAccessor` pattern.
3. **Opt-in (`enabled` defaults false).** *Why:* the feature writes outside the repo into a user's
   iCloud folder; it must not start mirroring or auto-running without explicit consent.
4. **Summaries cover every completion, not only imported tasks.** *Why:* the "monitor from your phone"
   half is the point — you want to see all results. Import is the only Obsidian-specific ingress.
5. **Auto-run via the existing drain, suppressed by Pause.** *Why:* the drain already runs serially
   "one at a time" with planning, needs-review, and circuit-breaker handling; reusing it avoids
   duplicating run logic. `PauseRequested` is the kill switch / "import but hold" mode.
6. **Idle-gated, reentrancy-guarded, file-stability-debounced.** *Why:* never scan/import mid-run
   (`IsBusy`/`_runningTaskIds`), never overlap cycles, and never import a half-synced iCloud file
   (skip files newer than ~10 s and `.icloud` placeholders).
7. **Two idempotency layers on import** (frontmatter stamp + move to `Recognized/`) and **overwrite on
   export**. *Why:* iCloud races and app restarts must never double-create a task or strand a stale
   summary.
8. **Live export at the two terminal hooks + a reconcile pass.** *Why:* prompt summaries for normal
   completions, with the scan-time reconcile as the safety net for missed events and pre-existing
   completed tasks.
9. **Folders are auto-created and seeded with `INFO.md`.** *Why:* the user shouldn't have to make any
   folders, and iCloud/Obsidian won't reliably surface an empty folder — a seeded `INFO.md` makes each
   folder appear on every device and doubles as in-context documentation. Seeding never overwrites an
   edited `INFO.md`, and `INFO.md`/`README.md` are excluded from import so a guide is never run as a task.
10. **Out of scope (v1):** mirroring in-flight/pending tasks or live per-stage progress into the vault,
    and any non-macOS default path. *Why:* keep one tight, decided direction; the settled artifact is a
    completion summary, and the vault layout the user described is only `New Tasks/` + `Completed/`.

## Done when

- New unit tests pass and fail against today's (absent) code first: `ObsidianBridgeSettings` round-trips
  under an injected temp HOME and defaults to disabled; `ObsidianVaultLayout` composes the documented
  paths and `yyyy-MM-dd` date folders and `EnsureScaffold` creates the folders + seeds the four
  `INFO.md` files without clobbering an edited one on re-run; `ObsidianTaskImporter` turns a stable
  `New Tasks/*.md` into `llm-tasks/<slug>/<slug>.md`, stamps + moves the source, is idempotent on
  re-scan, skips unstable / placeholder files and the reserved `INFO.md`/`README.md`, and resolves or
  safely reports slug collisions; `ObsidianSummaryWriter` renders the
  template (stage table, commit/cost/duration, embedded spec), dates by the max stage timestamp, and
  overwrites on re-export; a VM test drives `RunObsidianBridgeScanAsync()` against a temp vault + temp
  repo and asserts import + summary without launching swival; the bridge tick no-ops while `IsBusy` /
  not enabled / paused (auto-run suppressed).
- Live behavior (drivable via the control API per `AGENTS.md`): with the bridge enabled and pointed at a
  vault, dropping a markdown file in `<repo>/New Tasks/` causes — within one poll, while idle — a new
  task to appear, the source to move to `New Tasks/Recognized/` stamped with a `vr-recognized` GUID, and
  (unless paused) the queue to drain; when any task finishes, `Completed/<date>/<id>.md` appears with its
  summary. Disabling the feature stops all of it. Missing folders are created automatically and each
  main folder gets an `INFO.md` guide (so the folders show up on every device); an `INFO.md` is never
  imported as a task.
- `./visual-relay check` is green; every changed/added C# and XAML file is < 300 lines (extract a child
  control if `SettingsPanel.axaml` would overflow); Conventional Commit subject, e.g.
  `feat(app): obsidian task bridge — import new tasks and publish run summaries`.
- **Self-contained:** the implementer sees only this file. No dependency on other queued tasks; it lands
  green on its own. This is a large task — it is a fair candidate for the per-task 10× turn-budget /
  boost-turns toggle if the run is turn-bound.
