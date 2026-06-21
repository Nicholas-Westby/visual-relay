## Stage 1 - Ideate

{
  "summary": "Build the Obsidian bridge feature bottom-up: 5 pure Core classes (settings, vault layout, importer, summary writer) + VM integration + Settings UI + Control API commands. Design is fully specified — implement exactly as described, no alternatives. Recommend Option A (bottom-up by dependency layer) for clean TDD flow, test isolation, and the ability to land green after each step.",
  "options": [
    "Option A — Bottom-up by dependency layer (recommended): settings → vault layout → importer → summary writer → VM → UI → API. Tests first at each step.",
    "Option B — End-to-end slice first: write the VM integration test red, then backfill all Core classes to make it pass. Extract unit tests for edge cases after.",
    "Option C — Parallel per-class with shared type catalogue: define all records/types upfront, then implement all 5 Core classes in parallel subagents, then serially wire VM + UI."
  ]
}

## Stage 2 - Research

{
  "findings": "All key references in the task spec have been verified against the actual source code. `RelayTaskRepository.Walk` (line 165) emits one task per subfolder via `EmitSingleTaskFromFolder`; the canonical markdown is `<folderName>/<folderName>.md`. `SkippedDirectories = [\"completed\", \"_ideation\"]` (line 9). `IsSkippedName` (line 287) checks `DONE-`/`IGNORE-` prefixes. `ListAsync` sorts by `Id` ordinal (line 41). `RelayTaskWriter.Slugify` (line 13), `ValidateSlug` (line 33), `CreateAsync` (line 83) are all static methods as described. `KeyEnvFile` resolves via `ResolvePath(string? xdgConfigHome, string? home)` → `XdgConfig.ResolveConfigDir` + `Path.Combine(…, \"visual-relay\", \".env\")` (line 49-53), uses `IEnvironmentAccessor` seam (line 32-34), creates dirs `0700` and files `0600` (lines 129-135, 141-145). `RelayRunHistory.ReadTaskMetric` (line 12) returns `TaskRunMetric` with `Stages`; `ReadStatusRecord` (line 118) returns `IReadOnlyList<StageStatusEntry>`. `StageRunMetric` has `StageNumber, StageName, Tier, Model, Timestamp, DurationSeconds, CostUsd, Turns` plus `CostLabel`/`DurationLabel`. `TaskRunMetric` has `CostUsd, DurationSeconds, CompletedStageCount, SummaryLabel`. `RelayTaskOutcome` is `(TaskId, Status, TaskHash, CommitSha, Reason)` with status enum `Committed | Flagged | Failed | Planned`. `RunOneAsync` (Execution.cs line 252) awaits `driver.RunTaskAsync` then `LoadRunHistoryAsync`. `CreateDrainLifecycleCallbacks` (LiveState.cs line 11) has `OnExecuteCompleted` at line 42. `DispatcherTimer` pattern: `StartBackendMonitoring` (line 254, 15s) and `StartElapsedTimer` (line 269, 1s), both called from `App.axaml.cs` (lines 39-40). `IsBusy` field at line 189, `_runningTaskIds` HashSet at line 35, `PauseRequested` at line 81. `DrainQueueAsync` (Execution.cs line 57) is `[RelayCommand(CanExecute = nameof(CanDrain))]`, routes through `EnsureRunnableAsync(null)` (line 65). `RootFolderDisplay.Name(rootPath)` returns folder name. `_folderPicker.PickFolderAsync()` is `IFolderPicker` interface. `ControlApi.ResolveCommand` (ControlApi.cs line 28) switches on known names, `BuildCommandsMap` (State.cs line 69) produces enabled map. The `SettingsPanel.axaml` is 232 lines currently (under 300). `IEnvironmentAccessor` has single method `GetEnvironmentVariable(string)`. `DictionaryEnvironmentAccessor` is the test double. `TestRepository` creates temp dir with `Guid.NewGuid().ToString(\"N\")`, disposes via `TestFileSystem.DeleteDirectoryResilient`. `AvaloniaFact` tests use `[Collection(\"Headless\")]`. `XdgConfig.ResolveConfigDir` throws if neither `XDG_CONFIG_HOME` nor `HOME` is set. `UiStateStore` provides a pattern for JSON config files in XDG config directory. `CompletionTimeResolver` uses 4-tier fallback (existing, relay dir mtime, git, markdown mtime). The `.relay/` directory contains per-task subdirectories with `obsidian-task-bridge/` already listed. The file size guard at `tools/guards/check-file-size.sh` enforces a 300-line limit per `.cs` and `.axaml` file.",
  "constraints": [
    "Every C# and XAML file must stay under 300 lines (enforced by tools/guards/check-file-size.sh). If SettingsPanel.axaml would exceed, extract ObsidianSettings.axaml as a child control.",
    "Tests that touch the Avalonia UI thread must use [AvaloniaFact] and carry [Collection(\"Headless\")] so they serialize on the shared dispatcher. Pure Core tests use plain [Fact] and can run in parallel.",
    "Timer-based VM code must follow the existing pattern: timers started only from App.axaml.cs, never from ctor or LoadInitialAsync; tests drive the work directly by calling the cycle method.",
    "Settings must persist to per-machine XDG config ($XDG_CONFIG_HOME/visual-relay/obsidian.json, fallback $HOME/.config/visual-relay/obsidian.json), not .relay/config.json, because the repo is shared with a VM.",
    "The XDG config resolution pattern must mirror KeyEnvFile: use IEnvironmentAccessor seam, ResolvePath(xdgConfigHome, home) static method, create dirs 0700 and files 0600.",
    "ObsidianBridgeSettings defaults enabled=false (opt-in), vaultRoot expands ~/$HOME, pollSeconds default 60 clamped ≥15.",
    "Non-macOS / unset $HOME must degrade to disabled rather than throwing.",
    "INFO.md and README.md (case-insensitive) must be excluded from task import; the reserved set is exposed by ObsidianVaultLayout.",
    "Import must debounce by minStableAge (~10s), skip .icloud placeholders and zero-length sentinels, and skip files already carrying vr-recognized frontmatter.",
    "Slug collisions on import must be resolved by suffixing -2, -3, etc. using ValidateSlug, with a bounded retry limit; unresolvable collisions leave the source file untouched (not stamped, not moved).",
    "Completion date for summaries must use max stage Timestamp → fallback chain, never file mtime for authoritative sorting.",
    "The bridge timer must be idle-gated (IsBusy || _runningTaskIds.Count > 0 || IsSettingsOpen || IsEditingMarkdown || IsNewTaskDialogOpen), reentrancy-guarded with _bridgeCycleBusy, and file-stability-debounced.",
    "Auto-run after import uses the existing DrainQueueCommand (self-gates via CanDrain/EnsureRunnableAsync) and is suppressed by PauseRequested.",
    "Live export happens at both terminal hooks (RunOneAsync after outcome, and OnExecuteCompleted in drain lifecycle), guarded by _obsidianEnabled, best-effort.",
    "The scaffold must create folders + seed INFO.md idempotently (never overwrite a user-edited INFO.md). Seeding happens on first enabled scan, not at startup.",
    "ObsidianSummaryWriter maps status: Committed→committed, Flagged→needs-review, Failed→failed; when outcome is null, infer from status record / NEEDS-REVIEW marker.",
    "Control API must register obsidian-scan and obsidian-bridge commands following the bypass-sandbox JSON body pattern.",
    "Vault errors must never break a run; everything is best-effort with errors surfaced via StatusText/run log.",
    "The iCloud default path is ~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/ (macOS only)."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The Obsidian task bridge feature has zero implementation — no ObsidianBridge/ directory, no symbols in any .cs file. All 9 dependency code points (RelayTaskRepository, RelayTaskWriter, KeyEnvFile, RelayRunHistory, StageRunMetric/TaskRunMetric, RelayTaskOutcome, StageStatusEntry/StageStatusRecord, terminal hooks RunOneAsync+OnExecuteCompleted, timer pattern StartBackendMonitoring/StartElapsedTimer) are verified present and match the task spec. The feature is fully specified across 7 build steps (5 Core classes, VM integration, Settings UI, Control API) and follows established patterns: XDG config via IEnvironmentAccessor seam (KeyEnvFile mirror), DispatcherTimer started only from App.axaml.cs, idle-gated + reentrancy-guarded bridge cycle, Control API property-action JSON body pattern, and <300-line file size guard.",
  "excerpts": [
    "grep ObsidianBridge|ObsidianVault|ObsidianTask|ObsidianSummary *.cs → 0 matches",
    "list_files src/VisualRelay.Core/ObsidianBridge → path does not exist",
    ".relay/obsidian-task-bridge/pre-run-untracked.txt → empty",
    "RelayTaskRepository.cs:9 → SkippedDirectories = [\"completed\", \"_ideation\"]; line 287 → IsSkippedName checks DONE-/IGNORE-",
    "RelayTaskWriter.cs:13 → Slugify; :33 → ValidateSlug; :83 → CreateAsync — all static, reusable",
    "KeyEnvFile.cs:49 → internal static ResolvePath(xdgConfigHome, home); uses IEnvironmentAccessor seam at :32; dirs 0700 files 0600 at :129-145",
    "RunMetrics.cs → StageRunMetric(StageNumber,StageName,Tier,Model,Timestamp,DurationSeconds,CostUsd,Turns+CostLabel/DurationLabel); TaskRunMetric(CostUsd,DurationSeconds,CompletedStageCount,SummaryLabel)",
    "RelayTaskOutcome.cs checksum=4f989e8e → (TaskId,Status,TaskHash,CommitSha,Reason) — Committed|Flagged|Failed|Planned",
    "StageStatus.cs → StageStatusEntry(Stage,Name,Status,Check,DurationSeconds,CostUsd,Turns,Model,Error) + StageStatusRecord.Read/WriteAsync",
    "Execution.cs:252 → RunOneAsync awaits driver.RunTaskAsync then LoadRunHistoryAsync; outcome at :270",
    "LiveState.cs:11 → CreateDrainLifecycleCallbacks; OnExecuteCompleted at :42",
    "MainWindowViewModel.cs:254 → StartBackendMonitoring; :269 → StartElapsedTimer — 'Called ONLY from App startup' pattern",
    "App.axaml.cs:39-40 → viewModel.StartBackendMonitoring(); viewModel.StartElapsedTimer();",
    "SettingsPanel.axaml → 232 lines (68 under 300 limit)",
    "ControlApi.cs:28 → ResolveCommand switch; :108 → bypass-sandbox JSON body pattern; State.cs:69 → BuildCommandsMap",
    "XdgConfig.cs:15 → ResolveConfigDir throws when neither XDG_CONFIG_HOME nor HOME set — bridge must degrade gracefully",
    "RelayTaskRepository.cs:45 → ListCompletedAsync exists for reconcile pass"
  ],
  "repro": "Search the codebase: grep -r 'ObsidianBridge\\|ObsidianVault\\|ObsidianTask\\|ObsidianSummary' --include='*.cs' src/ → 0 matches. The src/VisualRelay.Core/ObsidianBridge/ directory does not exist. The feature must be built from scratch in dependency order: ObsidianBridgeSettings → ObsidianVaultLayout → ObsidianTaskImporter → ObsidianSummaryWriter → MainWindowViewModel.ObsidianBridge.cs → SettingsPanel.axaml (+ObsidianSettings.axaml if needed) → ControlApi.cs/State.cs."
}

## Stage 4 - Plan

{
  "plan": "## Build plan — Obsidian task bridge (TDD, bottom-up)\n\n### Phase 1: ObsidianBridgeSettings (Core)\nCreate `src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs` as a static class mirroring `KeyEnvFile`:\n- Record `ObsidianBridgeConfig(bool Enabled, string VaultRoot, int PollSeconds)`, defaults `(false, \"~/Library/Mobile Documents/iCloud~md~obsidian/Documents/Visual Relay LLM Tasks/\", 60)`.\n- `internal static string ResolvePath(string? xdgConfigHome, string? home)` → `$XDG_CONFIG_HOME/visual-relay/obsidian.json` (or `$HOME/.config/…` fallback), reusing `XdgConfig.ResolveConfigDir`.\n- `static ObsidianBridgeConfig Load(IEnvironmentAccessor?)` — missing file/malformed → return defaults; expand `~`/`$HOME` in `VaultRoot`; clamp `PollSeconds` ≥ 15; if `HOME` is unset on non-Windows, `Enabled` stays/starts `false`.\n- `static void Save(ObsidianBridgeConfig, IEnvironmentAccessor?)` — write JSON, create dir `0700`, file `0600` like `KeyEnvFile.Upsert`.\n\n**Test:** `tests/VisualRelay.Tests/ObsidianBridgeSettingsTests.cs` — round-trips; missing file → defaults; `enabled` defaults false; unset HOME degrades to disabled; non-macOS still functional.\n\n### Phase 2: ObsidianVaultLayout (Core)\nCreate `src/VisualRelay.Core/ObsidianBridge/ObsidianVaultLayout.cs`:\n- Constructor `(vaultRoot, repoName)` — sanitizes `repoName` (strip `Path.DirectorySeparatorChar` / `AltDirectorySeparatorChar`; if empty → `\"project\"`).\n- Computed props: `RepoDir`, `NewTasksDir`, `RecognizedDir`, `CompletedRootDir`, `CompletedDir(DateOnly)`, `SummaryPath(taskId, DateOnly)`.\n- `public static IReadOnlySet<string> ReservedFileNames` → `[\"info.md\", \"readme.md\"]` (case-insensitive).\n- `void EnsureScaffold()` — creates `RepoDir`, `NewTasksDir`, `RecognizedDir`, `CompletedRootDir`; seeds `INFO.md` into each of the four folders **only when absent** (idempotent; never overwrites an existing file). Uses the templates from the task spec.\n\n**Test:** `tests/VisualRelay.Tests/ObsidianVaultLayoutTests.cs` — path composition; `CompletedDir` yields `yyyy-MM-dd`; sanitization of `repoName`; `EnsureScaffold` creates folders + 4 `INFO.md` files; re-run is a no-op and doesn't clobber a user-edited `INFO.md`.\n\n### Phase 3: ObsidianTaskImporter (Core)\nCreate `src/VisualRelay.Core/ObsidianBridge/ObsidianTaskImporter.cs`:\n- `record ImportCandidate(string FilePath, string Title, DateTimeOffset LastWrite)`.  \n- `IReadOnlyList<ImportCandidate> Scan(ObsidianVaultLayout layout, DateTimeOffset nowUtc, TimeSpan minStableAge)` — enumerates `NewTasksDir/*.md` top-level only; excludes `ReservedFileNames`, files with `vr-recognized:` frontmatter, `.icloud`/zero-length placeholders, files newer than `minStableAge`.\n- `record ImportResult(string? Slug, Guid? SourceGuid, string? RecognizedPath, string? SkipReason)`.\n- `ImportResult Recognize(ImportCandidate candidate, string rootPath, DateTimeOffset nowUtc, Guid newGuid)`:\n  1. Derive title: YAML `title:` → first `# H1` → filename (no ext).\n  2. `slug = RelayTaskWriter.Slugify(title)`; resolve collisions by suffixing `-2`, `-3`, … (bounded, e.g. 100 tries) using `RelayTaskWriter.ValidateSlug(slug, rootPath)`. Unresolvable → return skip result, leave file untouched.\n  3. Strip leading YAML frontmatter (`---\\n...\\n---`) from body; `RelayTaskWriter.CreateAsync(rootPath, slug, body)`.\n  4. Stamp source file frontmatter with `vr-task-id`, `vr-recognized`, `vr-recognized-at`, `vr-repo`.\n  5. Move to `RecognizedDir` (numeric suffix on name clash).\n- Markdown parsing: regex for frontmatter and title extraction.\n\n**Test:** `tests/VisualRelay.Tests/ObsidianTaskImporterTests.cs` — fresh file → `llm-tasks/<slug>/<slug>.md` with clean body, source stamped + moved; re-scan returns empty; file < `minStableAge` skipped; `.icloud` placeholder skipped; `INFO.md` skipped; collision suffixes; unresolvable collision reported.\n\n### Phase 4: ObsidianSummaryWriter (Core)\nCreate `src/VisualRelay.Core/ObsidianBridge/ObsidianSummaryWriter.cs`:\n- `string Build(string rootPath, string taskId, RelayTaskOutcome? outcome, string specMarkdown, Guid? sourceGuid, DateTimeOffset nowUtc)` — renders the summary template (frontmatter + status line + stages table + embedded spec).\n- `void Write(ObsidianVaultLayout layout, string rootPath, string taskId, RelayTaskOutcome? outcome, string specMarkdown, Guid? sourceGuid, DateTimeOffset nowUtc)` — resolves completion date (max stage `Timestamp` → newest `.relay/<id>` file mtime → `nowUtc`), calls `layout.CompletedDir(date)`, creates dirs, writes file; **overwrites** existing.\n- Status mapping: `Committed` → `committed`, `Flagged` → `needs-review`, `Failed` → `failed`; null outcome → infer from `NEEDS-REVIEW` file / status record.\n\n**Test:** `tests/VisualRelay.Tests/ObsidianSummaryWriterTests.cs` — committed metric renders sha/cost/duration/stage table/embedded spec; flagged → `needs-review` + reason; date folder = max stage timestamp; re-`Write` overwrites in place.\n\n### Phase 5: VM integration (App)\nCreate `src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs` (partial class):\n- Backing fields on `MainWindowViewModel.cs`: `_obsidianEnabled`, `_obsidianVaultRoot`, `_obsidianPollSeconds`, `_bridgeCycleBusy`, `_obsidianBridgeTimer`.\n- `[ObservableProperty]` bindings: `ObsidianEnabled`, `ObsidianVaultRoot`, `ObsidianPollSeconds` (with `On<Prop>Changed` → `ObsidianBridgeSettings.Save`).\n- `BrowseVaultRootAsync` command using `_folderPicker.PickFolderAsync()`.\n- `internal async Task<int> RunObsidianBridgeScanAsync()` — the testable cycle: guard `_obsidianEnabled && valid RootPath`, build layout, `EnsureScaffold()`, `Scan`/`Recognize` each candidate, export reconcile (for recently completed tasks missing a summary), return import count. Best-effort: catch + surface via `StatusText`.\n- `StartObsidianBridge()` — `DispatcherTimer` at `_obsidianPollSeconds`; tick handler: skip if not enabled / not idle (`IsBusy || _runningTaskIds.Count > 0 || IsSettingsOpen || IsEditingMarkdown || IsNewTaskDialogOpen`) / `_bridgeCycleBusy`; otherwise `await RunObsidianBridgeScanAsync()`; if imported ≥ 1 && !`PauseRequested` → `DrainQueueCommand.ExecuteAsync(null)`.\n- **Export on completion hooks** (in `MainWindowViewModel.Execution.cs` and `MainWindowViewModel.LiveState.cs`): call `ObsidianSummaryWriter.Write(…)` in `RunOneAsync` after `outcome` is known (line ~271), and in `CreateDrainLifecycleCallbacks`'s `OnExecuteCompleted` (line ~42). Both guarded by `_obsidianEnabled`, best-effort.\n- Modify `App.axaml.cs` (line ~40): add `viewModel.StartObsidianBridge();`.\n\n**Test:** `tests/VisualRelay.Tests/ObsidianBridgeVmTests.cs` — `[AvaloniaFact]` VM test: temp vault + temp repo; call `RunObsidianBridgeScanAsync()` directly; assert task imported + summary written; no-op when disabled/idle; auto-run suppressed by Pause.\n\n### Phase 6: Settings UI\n- Create `src/VisualRelay.App/Views/Controls/ObsidianSettings.axaml` (child control) with: enable CheckBox bound to `ObsidianEnabled`, vault-root TextBox bound to `ObsidianVaultRoot` + Browse button (`BrowseVaultRootCommand`), poll-seconds NumericUpDown or TextBox bound to `ObsidianPollSeconds`, one-line explanation TextBlock.\n- Create `src/VisualRelay.App/Views/Controls/ObsidianSettings.axaml.cs` (code-behind, `InitializeComponent()` only).\n- Add `<controls:ObsidianSettings/>` to `SettingsPanel.axaml` before the closing `</StackPanel>` (line ~228), with a section header. Keep the file under 300 lines; if it would overflow, the extracted child control keeps it safe.\n\n### Phase 7: Control API\n- `ControlApi.cs` `ResolveCommand` switch: add `\"obsidian-scan\"` → `null` (it's a property action, not an ICommand).\n- `ControlApi.cs` `PropertyActions` array: add `\"obsidian-scan\"` and `\"obsidian-bridge\"`.\n- `ControlApi.cs` `InvokePropertyAction` switch: add cases for `\"obsidian-scan\"` (fire-and-forget `viewModel.RunObsidianBridgeScanAsync()`) and `\"obsidian-bridge\"` (JSON body `{\"value\":true|false}` → `viewModel.ObsidianEnabled`, `{\"path\":\"…\"}` → `viewModel.ObsidianVaultRoot`).\n- `ControlApi.State.cs` `BuildCommandsMap`: add `obsidian-scan` (enabled when `vm.ObsidianEnabled && !vm.IsBusy`) and `obsidian-bridge` (always enabled).\n- `ControlApi.State.cs` `BuildStateSnapshot`: add `obsidianBridge` object with `enabled`, `vaultRoot`, `pollSeconds`.\n\n**Test:** `tests/VisualRelay.Tests/ControlApiTests.cs` — extend existing tests to cover new commands. Add assertions in `BuildStateJson_IncludesCommandsMapAndStages` for the new command names.\n\n### Done-when checklist\n- `./visual-relay check` green.\n- Every changed/added file < 300 lines.\n- All new tests pass (and fail red before implementation).\n- Conventional Commit: `feat(app): obsidian task bridge — import new tasks and publish run summaries`.",
  "manifest": [
    "+src/VisualRelay.Core/Configuration/ObsidianBridgeSettings.cs",
    "+tests/VisualRelay.Tests/ObsidianBridgeSettingsTests.cs",
    "+src/VisualRelay.Core/ObsidianBridge/ObsidianVaultLayout.cs",
    "+tests/VisualRelay.Tests/ObsidianVaultLayoutTests.cs",
    "+src/VisualRelay.Core/ObsidianBridge/ObsidianTaskImporter.cs",
    "+tests/VisualRelay.Tests/ObsidianTaskImporterTests.cs",
    "+src/VisualRelay.Core/ObsidianBridge/ObsidianSummaryWriter.cs",
    "+tests/VisualRelay.Tests/ObsidianSummaryWriterTests.cs",
    "+src/VisualRelay.App/ViewModels/MainWindowViewModel.ObsidianBridge.cs",
    "+tests/VisualRelay.Tests/ObsidianBridgeVmTests.cs",
    "+src/VisualRelay.App/Views/Controls/ObsidianSettings.axaml",
    "+src/VisualRelay.App/Views/Controls/ObsidianSettings.axaml.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.LiveState.cs",
    "src/VisualRelay.App/ViewModels/MainWindowViewModel.Settings.cs",
    "src/VisualRelay.App/Views/Controls/SettingsPanel.axaml",
    "src/VisualRelay.App/Services/ControlApi.cs",
    "src/VisualRelay.App/Services/ControlApi.State.cs",
    "src/VisualRelay.App/App.axaml.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/ObsidianBridgeSettingsTests.cs",
    "tests/VisualRelay.Tests/ObsidianVaultLayoutTests.cs",
    "tests/VisualRelay.Tests/ObsidianTaskImporterTests.cs",
    "tests/VisualRelay.Tests/ObsidianSummaryWriterTests.cs",
    "tests/VisualRelay.Tests/ObsidianBridgeVmTests.cs"
  ],
  "rationale": "All 5 test files reference types/namespaces that don't exist yet (ObsidianBridgeSettings, ObsidianBridgeConfig, ObsidianVaultLayout, ObsidianTaskImporter, ImportCandidate, ImportResult, ObsidianSummaryWriter, and VM properties/methods). The build fails with 6 unique CS0234/CS0246 errors — CS0234 for the missing `VisualRelay.Core.ObsidianBridge` namespace, CS0246 for the `ObsidianVaultLayout` type used across tests. Once the namespace and classes are created, additional CS0103/CS0246 errors will surface for the specific methods/properties/records that don't yet exist (ResolvePath, Load, Save, EnsureScaffold, Scan, Recognize, Build, Write, RunObsidianBridgeScanAsync, ObsidianEnabled, ObsidianPollSeconds, etc.). Tests follow the existing patterns: plain [Fact] for pure Core tests, [AvaloniaFact] with [Collection(\"Headless\")] for VM tests, TestRepository for temp repos, DictionaryEnvironmentAccessor for injected env, temp directories cleaned via TestFileSystem.DeleteDirectoryResilient."
}

## Stage 6 - Implement

{ "summary": "Implemented Obsidian task bridge feature across 7 phases, all TDD. Created 4 Core classes (ObsidianBridgeSettings, ObsidianVaultLayout, ObsidianTaskImporter, ObsidianSummaryWriter), VM integration (MainWindowViewModel.ObsidianBridge.cs), Settings UI (ObsidianSettings.axaml), and Control API commands (obsidian-scan, obsidian-bridge). Hooked into RunOneAsync and OnExecuteCompleted for live export on completion. Split oversized test files to stay under 300-line guard. All 1517 tests pass, 0 failures, 13 skipped (pre-existing). Key decisions: used direct file I/O instead of sync-over-async in Recognize to avoid deadlocks; collapsed ObsidianSettings into compact single-border layout to fit settings modal; derived repo name from rootPath for vr-repo stamp; best-effort error handling throughout." }

## Stage 7 - Review

{
  "verdict": "changes",
  "issues": [
    "ObsidianTaskImporter.Recognize manually writes llm-tasks/<slug>/<slug>.md instead of calling RelayTaskWriter.CreateAsync(rootPath, slug, body) as the spec requires. It should reuse the canonical writer verbatim (and become async / await CreateAsync rather than duplicating directory/file creation).",
    "MainWindowViewModel.RunObsidianBridgeScanAsync imports tasks but never calls ReloadTaskListAsync, so newly imported tasks do not appear in the UI until the next refresh. The spec explicitly requires marshaling this reload to the UI thread.",
    "Views/Controls/ObsidianSettings.axaml is missing the required one-line explanation that files in New Tasks/ become tasks and summaries publish to Completed/.",
    "MainWindowViewModel.ObsidianBridge.cs reimplements task-spec discovery in ExportSummaryOnCompletion/ResolveTaskSpec and ReconcileExportsAsync instead of resolving the current/DONE-/archived path via RelayTaskRepository as the spec requests.",
    "ReconcileExportsAsync iterates over every completed task with no cap; the spec says the reconcile pass should be bounded to a reasonable recent count.",
    "ObsidianTaskImporter.Recognize contains dead/unused ObsidianVaultLayout construction and a confusing inline comment; it should be cleaned up after switching to RelayTaskWriter.CreateAsync.",
    "ObsidianSummaryWriter formats vr-cost-usd and the status-line cost with :F2, which rounds sub-cent costs to $0.00 and conflicts with the stage-table cost labels that preserve small values.",
    "StartObsidianBridge uses an async void DispatcherTimer.Tick lambda; for consistency with StartBackendMonitoring it should use a named async method with the returned task discarded (_ = OnBridgeTickAsync())."
  ]
}

## Stage 8 - Fix

{ "summary": "Resolved all 8 review issues: (1) ObsidianTaskImporter.Recognize now calls RelayTaskWriter.CreateAsync instead of manual file creation, and is async; (2) RunObsidianBridgeScanAsync calls ReloadTaskListAsync after importing so new tasks appear in the UI; (3) ObsidianSettings.axaml now shows a one-line explanation that files in New Tasks/ become tasks and summaries publish to Completed/; (4) ExportSummaryOnCompletion and ReconcileExportsAsync both resolve task specs via RelayTaskRepository (using task.MarkdownPath from ListCompletedAsync / ListAsync) instead of reinventing path discovery; (5) ReconcileExportsAsync is bounded to .Take(50) recent completed tasks; (6) dead ObsidianVaultLayout construction and confusing comment removed from Recognize; (7) ObsidianSummaryWriter uses MoneyFormatter.Dollars() instead of :F2 so sub-cent costs don't round to $0.00; (8) StartObsidianBridge uses a named async OnBridgeTickAsync method instead of an async void lambda. All 1517 tests pass, 0 failures, 13 pre-existing skips." }

## Stage 9 - Verify

{
  "summary": "Obsidian task bridge feature completed — all 8 review fixes applied, 1517 tests pass (0 failures), all files under 300-line limit, check green. Feature provides bidirectional bridge: ingress from New Tasks/ via file-scan + stamp-and-move, egress as Completed/<date>/<id>.md summaries at both terminal hooks and reconcile pass, with per-machine XDG config, idle-gated timer, auto-run via existing drain, Settings UI, and Control API commands.",
  "commitMessages": [
    "feat(app): obsidian task bridge — import tasks from vault and publish run summaries",
    "feat(obsidian): add bidirectional bridge for iCloud-synced task ingress and egress",
    "feat(core): implement ObsidianBridgeSettings, VaultLayout, TaskImporter, and SummaryWriter",
    "feat(app): integrate obsidian bridge into VM with idle-gated timer, settings, and control API",
    "feat: obsidian vault bridge — file-based remote control for Visual Relay"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

