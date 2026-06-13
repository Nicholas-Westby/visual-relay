# Add a Settings panel (cog) that can keep Visual Relay's `.relay/` run proof out of repo commits

Visual Relay gitignores its own run scratch under `.relay/` (`.gitignore` ignores
`.relay/*` except `!.relay/config.json`, and target repos get a `.relay/.gitignore` from
`RelayGitignoreWriter`), yet every successful run **force-commits** a proof subset back
into the repo anyway. Some users don't want Visual Relay's bookkeeping landing in their
history. Add a per-repo setting — opened from a new **cog / Settings** surface in the top
bar — that turns this off. **Committing the proof stays the default** (today's behavior);
the toggle only lets a user opt out.

> **Implementation order:** this is task **01** of a batch and is foundational. Two later
> tasks build on it — `02-per-task-10x-turn-budget-toggle` mirrors the per-repo config field
> added here, and `03-consolidate-provider-keys-into-settings-panel` moves the Provider Keys
> UI into the `SettingsPanel` + cog created here. Build task 01 first.

## Current state (researched)

### What actually lands in commits, and where
- The **only** path that puts `.relay/` content into a commit is the force-add of proof
  files: `GitCommitter.CommitAsync` runs `git add -f -- <proofFiles>`
  (`src/VisualRelay.Core/Execution/GitCommitter.cs:92-99`; rationale comment `:88-91`).
  Everything else is strict: manifest staging uses `git add -A` and pre-rejects gitignored
  manifest paths (`GitCommitter.cs:50-66,68-75`); the untracked auto-include explicitly
  skips `.relay/`, `.relay-scratch/`, `.swival/` via `IsInternalArtifact`
  (`GitCommitter.cs:9,110-130,258-265`). So `.relay` remnants enter history **exclusively**
  through the proof force-add.
- The proof list is assembled in `RelayDriver.ExecuteCommitStageAsync`
  (`src/VisualRelay.Core/Execution/RelayDriver.CommitGate.cs:144-150`):
  `.relay/<taskId>/ledger.md`, `.relay/<taskId>/<taskId>.seals`,
  `.relay/<taskId>/manifest.txt`, `.relay/<taskId>/status.json`. `config` (RelayConfig) is
  already in scope here (`CommitGate.cs:125`).
- **Important distinction — task completion is separate from `.relay` proof.** Completion
  works by renaming `llm-tasks/<id>.md` → `DONE-<id>.md` (or moving it into
  `completed/batch-N/`) on disk; discovery then skips `DONE-`/`IGNORE-` names
  (`RelayTaskRepository.cs:288-290`), so a retired task never re-enters the pending queue.
  That rename **must be committed with the run** or a later `git checkout`/`reset` restores
  the open `<id>.md` from HEAD and the task re-runs (real incident documented in
  `llm-tasks/DONE-commit-task-retirement-with-the-run.md`; discovery hardening in
  `llm-tasks/DONE-folder-task-done-residue-resurrects-as-pending.md`). It is committed via
  two paths that have nothing to do with `.relay/`: `git add -u` stages the **deletion** of
  `llm-tasks/<id>.md` (`GitCommitter.cs:82`), and the **addition** of the `DONE-`/archived
  file rides in through `retirement.Additions` (`CommitGate.cs:151-152`, force-added at
  `GitCommitter.cs:94`). Those additions live under the tasks dir, **not** `.relay/`
  (`TaskCompletionArchive.cs:54,64-67,114,131`) — they are not remnants and must keep being
  committed regardless of the toggle, so completed tasks never resurrect.

### Per-repo settings precedent — `BypassSandbox` (mirror it exactly)
- Field on the config record: `bool BypassSandbox = false` (`src/VisualRelay.Domain/RelayConfig.cs:39`).
- Parsed in `RelayConfigLoader.TryLoadAsync`:
  `BypassSandbox = OptionalBool(root, "bypassSandbox", defaults.BypassSandbox)`
  (`src/VisualRelay.Core/Configuration/RelayConfigLoader.cs:141`); `Defaults(...)` sets it
  (`:39`).
- Written via read-modify-write that preserves all other keys:
  `RelayConfigWriter.UpsertBypassSandbox` (`src/VisualRelay.Core/Init/RelayConfigWriter.cs:45-66`).
- Surfaced as `[ObservableProperty] bool _bypassSandbox` with `OnBypassSandboxChanged`
  → upsert (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Settings.cs:12-22`), and
  hydrated from the loaded config at
  `src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs:101-104`.

### UI settings-surface precedent — the "Keys" flyout (mirror it)
- The TopBar "Keys" button opens a `Button.Flyout` hosting the `KeySetupPanel` UserControl,
  toggled by `ToggleKeySetupCommand` (`src/VisualRelay.App/Views/Controls/TopBar.axaml:91-105`).
  The TopBar layout grid is `ColumnDefinitions="Auto,*,Auto,Auto,Auto,Auto,Auto,Auto"`
  (`TopBar.axaml:11`). Shared styles (`Border.panel`, `panelTitle`, `Button.primary`, …)
  live in `Styles/VisualRelayTheme.axaml`.
- App is Avalonia 12.0.4, CommunityToolkit.Mvvm 8.4.1, compiled bindings + warnings-as-errors.

## What to build

TDD — write the failing test first.

### 1. Config plumbing
- Add `bool CommitProofArtifacts = true` to `RelayConfig` as a **trailing optional**
  (default `true` = current behavior). Document it like the other flags.
- Parse it in `RelayConfigLoader.TryLoadAsync` as
  `CommitProofArtifacts = OptionalBool(root, "commitProofArtifacts", defaults.CommitProofArtifacts)`.
- Add `RelayConfigWriter.UpsertCommitProofArtifacts(rootPath, bool)` mirroring
  `UpsertBypassSandbox` (preserve every existing key).

### 2. Gate the proof force-add (`RelayDriver.CommitGate.cs:144-152`)
- When `config.CommitProofArtifacts` is **false**, build `proofFiles` **without** the four
  `.relay/<taskId>/…` entries; when **true**, include them exactly as today.
- **Always** keep `retirement.Additions` in `proofFiles` regardless of the flag (task
  lifecycle record — see the resurrection note above).
- Leave `GitCommitter` generic — it already no-ops the force-add when `proofFiles` is empty
  (`GitCommitter.cs:92`). The commit still carries code changes, retirement, and the
  `Relay-Seal:` trailer (`GitCommitter.cs:135`).
- **Scope — the toggle changes only what is *committed*.** The `.relay/<id>/` files are still
  written to disk every run, so local resume, re-added-task detection, and the `DONE-`
  retirement keep working; opting out only means a fresh clone won't carry the run's
  seal/ledger proof in history.

### 3. UI: a Settings panel opened by a cog
- New `Views/Controls/SettingsPanel.axaml` (+`.axaml.cs`), `x:DataType="vm:MainWindowViewModel"`,
  styled like `KeySetupPanel`, containing one clearly-labeled `CheckBox` bound to a new
  `[ObservableProperty] bool _commitProofArtifacts = true`. Suggested copy:
  - Title: "Settings"
  - Checkbox: "Commit Visual Relay's run proof to this repo"
  - Sub-text: "Forces the `.relay/` seal, ledger, manifest, and status files into each
    commit so every run stays verifiable even though `.relay/` is gitignored. Uncheck to
    keep Visual Relay's run files out of your commits."
- Add a cog `Button` to the TopBar (`TopBar.axaml`) with a `Flyout` hosting `SettingsPanel`,
  mirroring the Keys button (add one more `Auto` column to `:11`, place a ⚙ glyph + label).
  Add a `ToggleSettingsCommand` if mirroring `ToggleKeySetupCommand`.
- VM (`MainWindowViewModel.Settings.cs`): add `_commitProofArtifacts` with
  `OnCommitProofArtifactsChanged` → `RelayConfigWriter.UpsertCommitProofArtifacts(RootPath, value)`
  (guard `Directory.Exists(RootPath)` as BypassSandbox does), and hydrate it from the loaded
  config next to `BypassSandbox` in `Helpers.cs:101-104`.

## Done when
- **Default unchanged:** with the box checked (default / `commitProofArtifacts` absent), a
  completed run force-commits `.relay/<taskId>/{ledger.md,<taskId>.seals,manifest.txt,status.json}`
  exactly as today.
- **Opt-out works:** with the box unchecked, a completed run commits the code changes, the
  task retirement (`DONE-`/archive) record, and the `Relay-Seal:` trailer, but **no**
  `.relay/` path appears in the commit (`git show --stat` shows none). Completed tasks still
  leave the active queue and do **not** resurrect on the next load.
- The setting persists to `.relay/config.json` as `commitProofArtifacts` (other keys
  preserved), reloads into the checkbox when the repo is reopened, and is honored by the
  driver on the next run.
- A cog button in the TopBar opens a Settings panel (flyout) holding the clearly-labeled
  toggle.
- Tests first: `RelayConfigLoader` treats absent `commitProofArtifacts` as `true` and honors
  `false`; `UpsertCommitProofArtifacts` preserves existing keys; a commit-stage/`GitCommitter`
  test (mirroring the existing commit tests) asserts the four `.relay/` proof files are
  force-added when `true` and omitted when `false`, while retirement additions are staged in
  both cases; a headless UI test that the cog opens the panel and toggling writes config.
- `./visual-relay check` green; changed files < 300 lines; compiled bindings clean;
  Conventional Commit subjects (e.g. `feat(commit): make .relay proof commit opt-out via a Settings cog`).
