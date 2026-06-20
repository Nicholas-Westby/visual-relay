# Per-stage Prompt & Output Visibility — Right-Column Tabs

**Status:** Design / approved — implementation split into the `stage-visibility-1..4` task series.
**Date:** 2026-06-20
**Topic:** Make each pipeline stage's system prompt, assembled ("input") prompt, and output first-class in the UI.

## Problem & goal

Visual Relay runs an 11-stage LLM pipeline (Ideate → Research → … → Commit). Today the only window into what each stage was told and produced is the streaming **RUN LOG** and **LLM COMMANDS** panels — effectively long logs that humans rarely read end-to-end. The **system prompt** and the full **assembled prompt** are not surfaced in the UI at all.

**Goal:** make each stage's **System Prompt**, **Input Prompt** (the assembled prompt actually sent), and **Output** (the stage's structured result) first-class and readable on a per-stage basis, **before / during / after** a run — for at-a-glance understanding, transparency, and faster troubleshooting.

**Non-goals:** changing pipeline behavior, prompts, or stage contracts; redesigning the queue/task panels; reducing prompt size (a separate concern).

## Background — data sources

- **Stages** are defined in `src/VisualRelay.Core/Execution/RelayStages.cs`. Each has a static **system prompt** and an **output contract**, both public via `RelayStages.All` → `RelayStageDefinition` (`src/VisualRelay.Domain/RelayStageDefinition.cs`). Exception: stage 6 (Implement) may use `ConfirmImplementationSystemPrompt` when implementation was front-loaded (`RelayDriver.cs:88-89`).
- The **assembled/input prompt** is built by `SwivalSubagentRunner.BuildPrompt` (`ProcessRunners.Helpers.cs:95-132`) and passed to swival as an argv at stage start (`ProcessRunners.RunAsync.cs:101-104`). It is deterministic markdown with `##` sections: `## Task input`, `## Manifest`, optional `## Task context`, optional `## Log sources`, `## Prior stages` (the running ledger), the output-contract line, optional `## Failing verify output`, optional `## Verify command`. Measured sizes: median ~18 KB, p90 ~33 KB, max ~71 KB.
- The prompt is also persisted in `.relay/<task>/stage<N>-attempt<M>.report.json` under `task` — but only **at stage end**. Its `timestamp` field is the reliable completion time (file mtimes are unreliable: VM sync resets them).
- The **output** is the validated fenced-JSON contract block, recorded in `ledger.md` and present in the report's `result.answer`; it also streams live as trace entries.
- **UI:** the right column is `Views/Controls/ActivityColumn.axaml` (~165 lines) — two stacked `ListBox`es: RUN LOG (`Events`) and LLM COMMANDS (`TraceEntries`). Selecting a stage card sets `_selectedStageFilter`, which **already filters both panels by stage** (`SelectStage` / `ApplyLogFilter`). MVVM = CommunityToolkit.Mvvm; a 300-line-per-file guard is enforced.

## Design overview

Convert the right column from two stacked panels into a **single tabbed panel** with five tabs, short + grouped:

```
Run Log · Commands   ⏐   System · Input · Output
```

- **Run Log** / **Commands** — the existing `Events` / `TraceEntries` views, unchanged (still honor the stage filter and counts).
- **System / Input / Output** — new; scoped to the **selected stage**, set off from the first two by a divider.

All five tabs key off the existing stage selection, so clicking a stage card drives the whole panel.

### Stage-scoped tabs

Each new tab shows a **context header**: `<Type> · Stage NN (Name) · attempt K · <size>`.

**Empty / transitional states (per tab):**

| Situation | Message |
|---|---|
| No stage selected (any tab) | "Click a stage to see its system prompt, input prompt, and output." |
| System Prompt, any time | Always shown once a stage is selected — known before the run. |
| Input Prompt, stage not started | "Input prompt for Stage NN (Name) will appear once the stage starts." |
| Output, stage not complete | "Output for Stage NN (Name) will appear once the stage completes." |
| Driver stage (11 Commit) | "This stage runs git directly — no LLM prompt or output." |

### Rendering (structured, not a JSON blob)

- **System Prompt** — readable text. Shows the actual prompt used when a run exists (including the stage-6 front-loaded variant); otherwise the static default.
- **Input Prompt** — parsed into its `##` sections and rendered as collapsible sections; the large **Prior stages** ledger is collapsed by default; a Copy action and a raw-text toggle are available. The parser is tolerant (preamble + bare contract line become their own sections; unrecognized input falls back to a raw view).
- **Output** — generic field-wise rendering of the contract JSON (string → text, string[] → list, else → pretty JSON) with a raw-JSON toggle. No per-stage bespoke rendering in v1.

### Draggable divider

A `GridSplitter` between the center and right columns lets the panel widen. The width (and selected tab) persist to **XDG user config** (`<XDG_CONFIG_HOME or ~/.config>/visual-relay/ui-state.json`), never in-repo (host/VM share the working tree). Collapse-to-rail is preserved (store/restore the expanded width; clamp min/max).

## Data flow

- A new **`StageDetailViewModel`** (App) holds the selected stage's system-prompt text, parsed input-prompt sections, field-wise output, context header, and per-tab state. Kept as its own type + a `MainWindowViewModel.StageDetail.cs` partial to respect the 300-line guard on `MainWindowViewModel.cs`.
- On stage selection (or a live refresh event), lazily load the **latest attempt** — chosen by attempt **number** via `RelayAttempt.AttemptNumber`, never by file mtime: `stage<N>-attempt<M>.input.json` → system + input prompt; `report.json` `result.answer` → output. The **default** system prompt is already public via `RelayStages.All[...].SystemPrompt` (no new accessor); the **actual** prompt used (incl. the stage-6 variant) is captured in `input.json`.
- **Live ("during") support — the one backend addition:** at stage start the runner writes `stage<N>-attempt<M>.input.json` (`{systemPrompt, inputPrompt, timestamp}`) next to the report and emits a **metadata-only** `stage_input` event (byte counts + path, NOT the ~70 KB text). Live and history read the **same file**; the event just nudges the UI to reload the selected stage. System prompt needs no other backend change; output already streams via trace and finalizes from the report.
- **Attempt selection:** latest attempt by number. (Optional later: an attempt switcher for retried / fix-verify-looped stages.)

## Affected components

| File | Change |
|---|---|
| `Execution/StageInputArtifact.cs` (Core) — new | Write/read `stage<N>-attempt<M>.input.json`; latest-by-number helper |
| `Execution/ProcessRunners.RunAsync.cs` (Core) | Write the artifact + emit `stage_input` at invocation |
| `RelayEvent` event-sink handling (App) | Handle `stage_input` (metadata only) → refresh selected stage |
| `Services/AssembledPromptParser.cs` + `OutputFieldParser` (App) — new | Parse the assembled prompt into sections; parse output JSON into fields |
| `ViewModels/StageDetailViewModel.cs` (App) — new | Selected-stage detail state + lazy load |
| `ViewModels/MainWindowViewModel.StageDetail.cs` (App) — new | Wire selection + live events → `StageDetailViewModel` |
| `Views/Controls/ActivityColumn.axaml` | Two stacked panels → 5-tab `TabControl` host |
| `Views/Controls/RunLogView`, `CommandsView`, `StageSystemView`, `StageInputView`, `StageOutputView` — new | Extracted/added tab bodies (keeps files < 300 lines) |
| `Views/MainWindow.axaml` | Resizable right column + `GridSplitter`; single-collapse rail |
| `Configuration/UiStateStore.cs` (Core) — new | Persist activity width + tab to `ui-state.json` (XDG), reusing `KeyEnvFile` path resolution |

## Decisions (settled)

Open during brainstorming, now resolved (research-backed); the task series implements exactly these:

- **Live surfacing:** write `stage<N>-attempt<M>.input.json` at stage start + emit a metadata-only `stage_input` event; live and history read the same file. (Not: stuffing the prompt into the event; not: relying on `report.json`, which is written only at stage end.)
- **System prompt source:** defaults via public `RelayStages.All[...].SystemPrompt`; actual-used (incl. stage-6 variant) captured in `input.json`. No new accessor.
- **Layout:** content grid is `Auto,*,Auto` with the right column a fixed `Width=340` + a 36px content-swap rail; add a resizable column + `GridSplitter` while preserving the rail swap.
- **Persistence:** new `UiStateStore` → XDG `ui-state.json` (JSON), reusing `KeyEnvFile`'s path resolution; NOT the dotenv secrets file.
- **Latest attempt:** by `RelayAttempt.AttemptNumber`, never mtime.
- **Output rendering:** generic field-wise + raw toggle; no per-stage bespoke rendering in v1.

## Testing

- **Unit:** `StageInputArtifact` (path/round-trip/latest-by-number); `AssembledPromptParser` + `OutputFieldParser` (all section/field shapes, fallbacks); `UiStateStore` (defaults, round-trip, corrupt-file).
- **ViewModel:** `StageDetailViewModel` state transitions — no selection, before start, after complete, driver stage, retried → latest attempt.
- **Headless UI:** five tabs render; stage selection drives all tabs; empty-state messages; tab-index + width persistence round-trip.
- Run via `./test.sh` (persists logs/trx and prints failing test names).

## Implementation — ordered task series

Each task lands green on its own; the implementing agent sees one at a time, so coordination is restated in every task file.

1. `llm-tasks/stage-visibility-1-emit-prompts-at-stage-start` — backend: persist system+input prompt at stage start + `stage_input` event.
2. `llm-tasks/stage-visibility-2-stage-detail-viewmodel-and-prompt-parser` — App data layer: parsers + `StageDetailViewModel` + selection/live wiring.
3. `llm-tasks/stage-visibility-3-activity-column-tabs-and-rendering` — right column → 5-tab panel + System/Input/Output rendering.
4. `llm-tasks/stage-visibility-4-resizable-divider-and-width-persistence` — draggable `GridSplitter` + XDG width/tab persistence.
