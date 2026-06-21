# Add a StageDetailViewModel + assembled-prompt parser that powers per-stage System/Input/Output

Part of per-stage prompt/output visibility (full design: `docs/superpowers/specs/2026-06-20-per-stage-prompt-output-tabs-design.md`). This task builds the App-side **data layer** the new tabs will bind to: a `StageDetailViewModel` that, for the currently-selected stage, exposes the **system prompt**, the **input prompt parsed into sections**, the **output rendered field-wise**, a context header, and per-tab empty/transitional states. No XAML/tabs yet (task 3) — this is headless-testable logic that lands green on its own.

**Depends on `stage-visibility-1`** (it writes `stage{N}-attempt{M}.input.json` and emits the `stage_input` event). If task 1 is not merged when you start, complete it first.

This design is decided — implement exactly this, no alternatives.

## Current state (researched)

- Stage selection already scopes the right panels: a stage-card click runs `SelectStage(StageRowViewModel)` (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Commands.cs:243-265`), which sets `_selectedStageFilter` and calls `ApplyLogFilter()` (`MainWindowViewModel.Helpers.cs`) to filter `Events` + `TraceEntries` by `StageNumber`; clicking the selected stage again clears the filter.
- `StageRowViewModel` exposes `Number, Name, Tier, Status, ReportPath, TraceDirectory` and `SelectCommand`.
- Live events arrive at `HandleRelayEvent` (`MainWindowViewModel.Helpers.cs:11-46`); the new `stage_input` event (task 1) and the existing `stage_done` event arrive here, each carrying `StageNumber`.
- The selected task's artifacts live in `<root>/.relay/<taskId>/stage{N}-attempt{M}.report.json` and `.input.json`. Latest attempt = max `RelayAttempt.AttemptNumber` (`src/VisualRelay.Core/Traces/RelayAttempt.cs:31-35`), **never** file mtime.
- The assembled input prompt is deterministic markdown from `BuildPrompt` (`src/VisualRelay.Core/Execution/ProcessRunners.Helpers.cs:95-132`): a preamble (`# Relay stage N: Name`, `Task:`, `Working directory:`), then `## Task input`, `## Manifest`, optional `## Task context`, optional `## Log sources`, `## Prior stages` (the running ledger — the large part), then the bare output-contract line, optional `## Failing verify output`, optional `## Verify command`.
- The **default** system prompt + contract are public: `RelayStages.All` (`src/VisualRelay.Core/Execution/RelayStages.cs:7`) → `RelayStageDefinition.SystemPrompt` / `.OutputContract` (`src/VisualRelay.Domain/RelayStageDefinition.cs:3-11`). Stage 11 (Commit) has `Kind == "driver"`.
- MVVM = CommunityToolkit.Mvvm (`[ObservableProperty]`); views resolve by type-name via `ViewLocator`. `MainWindowViewModel.cs` is ~278 lines — put new state in the new VM and a small partial, not in `MainWindowViewModel.cs`.

## What to build

TDD — failing tests first. These are pure/VM tests (no Avalonia rendering needed); keep `StageDetailViewModel` free of Avalonia control types so it stays headless-testable.

1. **`AssembledPromptParser`** (pure; `src/VisualRelay.App/Services/AssembledPromptParser.cs`): `IReadOnlyList<PromptSection> Parse(string assembledPrompt)` where `PromptSection = (string Title, string Body, bool CollapsedByDefault)`. Split on lines beginning `## ` into `(Title, Body)`; the text before the first `## ` becomes a `"Header"` section; the bare contract line trailing the `## Prior stages` body becomes an `"Output contract"` section; `"Prior stages"` is returned with `CollapsedByDefault = true` (everything else false). Tolerant: empty/garbage input → a single `"Prompt"` section holding the raw text. Unit-test: a full prompt with every section; a minimal prompt (Task input + Prior stages + contract only); missing optional sections; empty input.
2. **`OutputFieldParser`** (pure; same folder or nested): given the stage output JSON string, produce `IReadOnlyList<OutputField>` where `OutputField = (string Label, OutputFieldKind Kind, string Value)` — top-level keys mapped as string→`Text`, string[]→`List` (joined for display), object/other→`Json` (pretty-printed) — plus expose the original as `RawJson`. Tolerant of non-JSON (one `Text` field holding the raw string). Unit-test against the Plan contract (`{plan, manifest[]}`), the Review contract (`{verdict, issues[]}`), and a non-JSON blob.
3. **`StageDetailViewModel`** (`src/VisualRelay.App/ViewModels/StageDetailViewModel.cs`): `[ObservableProperty]` members — `SystemPromptText`, `InputSections` (from the parser), `OutputFields`, `RawJson`, `Header` (e.g. `"Stage 06 (Implement) · attempt 1 · 22.1 KB"`), and `SystemState`/`InputState`/`OutputState` (enum `StageDetailState { NoStage, NotStarted, NotComplete, Ready, DriverStage }`). A `void Load(StageRowViewModel? stage, string? taskDirectory)` method:
   - no stage / no `taskDirectory` → all three states `NoStage`.
   - `stage.Tier`/`Kind` driver stage (Commit) → all `DriverStage`.
   - else, find the latest attempt by number. **System**: from the latest `.input.json` `systemPrompt` if present, else the static `RelayStages.All[N-1].SystemPrompt` → always `Ready`. **Input**: latest `.input.json` `inputPrompt` → parse → `Ready`; none yet → `NotStarted`. **Output**: latest `report.json` (`result.answer`, falling back to the ledger section) → parse → `Ready`; none yet → `NotComplete`. Set `Header` from stage number/name + attempt + input byte size.
4. **Wire to selection + live refresh** (new partial `src/VisualRelay.App/ViewModels/MainWindowViewModel.StageDetail.cs`): own a single `StageDetailViewModel StageDetail { get; }`. Call `StageDetail.Load(selectedStage, taskDirectory)` (a) at the end of `SelectStage` (on both select and clear — clear → `Load(null, …)`), and (b) from `HandleRelayEvent` when a `stage_input` or `stage_done` event arrives with `StageNumber == _selectedStageFilter`. Resolve `taskDirectory` the same way `RevealStageArtifactsCommand` already does.

## Done when

- `AssembledPromptParser`, `OutputFieldParser`, and `StageDetailViewModel` tests pass and fail against today's code first — covering every section shape, all five states, latest-attempt-by-number (not mtime), the field-wise output parse, and static-vs-persisted system prompt.
- Selecting a stage (and a live `stage_input`/`stage_done` for the selected stage) updates `StageDetail`. No XAML changes in this task.
- `./visual-relay check` green; files < 300 lines; Conventional Commit, e.g. `feat(app): stage-detail view model + assembled-prompt parser`.
- **Coordinate (implementer sees only this file):** task `stage-visibility-3` binds three tab views to `StageDetail` (System→`SystemPromptText`, Input→`InputSections`, Output→`OutputFields`+`RawJson`) and renders `Header` + the state messages from the design doc. Keep this VM Avalonia-free.
