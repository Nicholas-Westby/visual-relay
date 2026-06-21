## Stage 1 - Ideate

{
  "summary": "Build StageDetailViewModel + AssembledPromptParser + OutputFieldParser to power the per-stage system/input/output tabs. Four files across App/ViewModels (StageDetailViewModel.cs, MainWindowViewModel.StageDetail.cs) and App/Services (AssembledPromptParser.cs, OutputFieldParser.cs), plus three test files. Bottom-up TDD: pure parsers first, then ViewModel that consumes them, then wiring into SelectStage/HandleRelayEvent. The parsers split assembled prompt markdown at `## ` headings into PromptSection[], and map stage output JSON keys into OutputField[] by kind (Text/List/Json). StageDetailViewModel holds five observable state enums (NoStage/NotStarted/NotComplete/Ready/DriverStage) and Load() resolves the latest attempt artifact by attempt-number (not mtime). Dependency on StageInputArtifact (task 1) is already merged.",
  "options": [
    "Option 1 — Bottom-up TDD: AssembledPromptParser (+tests) → OutputFieldParser (+tests) → StageDetailViewModel (+tests) → MainWindowViewModel.StageDetail.cs wire. Recommended: pure functions are easiest to unit-test first, VM tests then exercise real parser+file I/O integration without mocks.",
    "Option 2 — Top-down VM-first with IStageInputReader abstraction: write StageDetailViewModel contract tests first, stub the file reader, fill in parsers later. More design overhead (YAGNI interface) but surfaces API issues from the consumer perspective earlier.",
    "Option 3 — All-in-one incremental: write all four source files then all tests. Fastest time-to-green for someone who internalized the full spec, but risks non-TDD discipline and harder debugging when a test fails across multiple untested layers."
  ]
}

## Stage 2 - Research

{
  "findings": "Codebase investigation complete. The project is a .NET 10 Avalonia desktop app (CommunityToolkit.Mvvm). Stage selection already works via `SelectStage(StageRowViewModel)` (`MainWindowViewModel.Commands.cs:243-265`) which sets `_selectedStageFilter` (int?, line 30 of `MainWindowViewModel.cs`). `ApplyLogFilter()` (`MainWindowViewModel.Helpers.cs:243-256`) refilters `Events` and `TraceEntries` by the filter. The `stage_input` event is emitted by task-1's `ProcessRunners.StageInput.cs` (line 45) but is NOT yet handled in the App — no code in `VisualRelay.App` references `stage_input` or `StageInputArtifact`. `StageInputArtifact` (Core) has `LatestPath(taskDirectory, stageNumber)` which finds the highest-attempt `.input.json` by attempt number (never mtime), and `TryRead(path, out data)` which returns `SystemPrompt` and `InputPrompt`. Report JSON has `result.answer` (full text output, may include fenced ```json block). `FencedJsonExtractor.Extract()` (Core) extracts JSON from fenced blocks. `RelayStages.All` (Core) is a static list of `RelayStageDefinition` records with `Number, Name, Tier, Kind, SystemPrompt, OutputContract`. Stage 11 has `Kind == \"driver\"`. `StageRowViewModel` exposes `Number, Name, Tier, Status, ReportPath, TraceDirectory` but NOT `Kind`. Task directory is resolved as `Path.Combine(RootPath, \".relay\", taskId)` where `taskId` comes from `SelectedTask?.Id` or `relayEvent.TaskId`. Tests use xUnit v3, no mocking library; filesystem isolation via `TestRepository.Create()` or inline `TempDirectory`. Test project references `VisualRelay.App`, `VisualRelay.Core`, `VisualRelay.Domain`. The `BuildPrompt` method (`ProcessRunners.Helpers.cs:95-132`) creates deterministic markdown with `##` sections: Header (preamble), Task input, Manifest, optional Task context, optional Log sources, Prior stages (the running ledger), Output contract line, optional Failing verify output, optional Verify command. The `Prior stages` section is typically the largest part. The `OutputContract` line is the bare contract line (e.g., `End your reply with a single fenced ```json block, nothing after it, matching: {\"plan\": string, \"manifest\": string[]}`) — it trails the `Prior stages` body.",
  "constraints": [
    "Files must stay under 300 lines each (enforced by .editorconfig/convention).",
    "`StageDetailViewModel` must be headless-testable — no Avalonia control types.",
    "No XAML/tabs in this task — pure data-layer logic only.",
    "Latest attempt must be resolved by `RelayAttempt.AttemptNumber` (number), never file mtime.",
    "Task 1 (StageInputArtifact + stage_input event) is already merged and available.",
    "`StageRowViewModel` does NOT expose `Kind` — must look up `RelayStages.All` by `Number` to check driver stage.",
    "No mocking library in tests — use filesystem-based test helpers (TestRepository, TempDirectory).",
    "`StageDetailViewModel` state enum must be `StageDetailState { NoStage, NotStarted, NotComplete, Ready, DriverStage }`.",
    "Output parsing: extract JSON from `result.answer` (using `FencedJsonExtractor`), fall back to ledger.md if answer is missing.",
    "`AssembledPromptParser` — split on lines beginning `## `; text before first `## ` = `\"Header\"` section; bare contract line trailing `## Prior stages` body = `\"Output contract\"` section; `\"Prior stages\"` section has `CollapsedByDefault = true`.",
    "`OutputFieldParser` — map JSON top-level keys: string → Text, string[] → List (joined for display), object/other → Json (pretty-printed). Expose raw as `RawJson`. Non-JSON input → single Text field with raw string.",
    "Wire into `SelectStage`: call `StageDetail.Load(selectedStage, taskDirectory)` at end (both select and clear).",
    "Wire into `HandleRelayEvent`: call `StageDetail.Load(...)` when `stage_input` or `stage_done` event arrives with `StageNumber == _selectedStageFilter`.",
    "Use `MainWindowViewModel.StageDetail.cs` partial — do not add to `MainWindowViewModel.cs` (keep < 300 lines).",
    "System prompt: from latest `.input.json` `systemPrompt` if present, else `RelayStages.All[N-1].SystemPrompt`.",
    "Input: latest `.input.json` `inputPrompt` parsed → Ready; none yet → NotStarted.",
    "Output: latest report.json `result.answer` (fenced JSON extracted) → Ready; none yet → NotComplete.",
    "Driver stage (Commit, Kind==\"driver\"): all three states = DriverStage (no LLM prompt or output).",
    "No stage / no taskDirectory: all three states = NoStage.",
    "Header format: e.g. `\"Stage 06 (Implement) · attempt 1 · 22.1 KB\"` — stage number/name + attempt + input byte size.",
    "`./visual-relay check` must be green; Conventional Commit message expected."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The run log for stage-visibility-2 (stage 2/Research) confirmed three pre-existing conditions that affect the implementation: (a) zero references to stage_input/StageInputArtifact in VisualRelay.App — grep at run.log line 184 returned 'No matches found', confirming HandleRelayEvent currently ignores stage_input; (b) no .input.json files exist in the current task directory — list_files at line 172: 'No files matched the pattern', so StageDetailViewModel.Load() must handle the empty state; (c) StageRowViewModel.ctor at StageRowViewModel.cs:23-35 stores Number/Name/Tier but never Kind, requiring a RelayStages.All lookup. Additionally, 14 distinct tests fail on the Windows VM (CommitMessageSanitizerHardeningTests, CommitLintRunnerTests, CommitMessageValidatorContextualTests, RewriteHistoryRunnerTests, AppMenuNameTests, SandboxedShellVerifyExecutionTests, SandboxedTestRunnerArgumentTests) — all pre-existing and unrelated to this feature. None affect the macOS test pass rate. All integration-point APIs (StageInputArtifact.LatestPath/TryRead, FencedJsonExtractor.Extract, RelayStages.All) are confirmed public/accessible.",
  "excerpts": [
    "[run.log:184] grep for stage_input|StageInputArtifact|StageInput in src/VisualRelay.App → 'No matches found.'",
    "[run.log:172] list_files *.input.json → 'No files matched the pattern.'",
    "[StageRowViewModel.cs:23-35] ctor stores Number, Name, Tier — no Kind property exposed.",
    "[RelayStages.cs:23] new(11, 'Commit', 'cheap', 'driver', ...) — Kind='driver' for stage 11.",
    "[StageInputArtifact.cs:69-90] LatestPath uses RelayAttempt.AttemptNumber, never mtime.",
    "[FencedJsonExtractor.cs:1] internal static class — accessible via InternalsVisibleTo.",
    "[TestDoubles.cs:145] TempDirectory is private sealed class — not shared."
  ],
  "repro": "1. Run ./visual-relay check on macOS (expected green). 2. Observe that VisualRelay.App has zero usings or references to StageInputArtifact. 3. Confirm the task directory .relay/stage-visibility-2-stage-detail-viewmodel-and-prompt-parser/ contains no *.input.json files."
}

## Stage 4 - Plan

{
  "plan": "## Bottom-up TDD implementation of StageDetailViewModel + AssembledPromptParser + OutputFieldParser\n\n### Files to create\n\n1. **`src/VisualRelay.App/Services/AssembledPromptParser.cs`** — pure static parser.\n   - `PromptSection` record: `(string Title, string Body, bool CollapsedByDefault)` — public, in same file.\n   - `static IReadOnlyList<PromptSection> Parse(string assembledPrompt)`:\n     - Split on lines beginning `## ` (regex `^## `, multiline).\n     - Text before first `## ` → `new PromptSection(\"Header\", text, false)`.\n     - Each `## Title\\nBody` pair → `PromptSection(Title.Trim(), Body.Trim(), CollapsedByDefault)` where `CollapsedByDefault` is `Title == \"Prior stages\"`.\n     - For the \"Prior stages\" section: its body is `<ledger>\\n\\n<contract line>`. Split the body at the last `\\n\\n` (blank-line separator). The first part stays as the \"Prior stages\" body; the last part becomes a new `PromptSection(\"Output contract\", contractLine.Trim(), false)`.\n     - Tolerant: empty/whitespace/garbage with no `## ` lines → single `PromptSection(\"Prompt\", rawText, false)`.\n     - Null input → empty list.\n\n2. **`src/VisualRelay.App/Services/OutputFieldParser.cs`** — pure static parser.\n   - `OutputFieldKind` enum: `{ Text, List, Json }` — public, in same file.\n   - `OutputField` record: `(string Label, OutputFieldKind Kind, string Value)` — public, in same file.\n   - `OutputParseResult` record: `(IReadOnlyList<OutputField> Fields, string RawJson)` — public, in same file.\n   - `static OutputParseResult Parse(string? stageOutput)`:\n     - Null/empty → empty Fields, empty RawJson.\n     - Try `FencedJsonExtractor.Extract(stageOutput)` first (since output may contain a fenced ```json block).\n     - If extract returns non-null JSON, use that; otherwise try `JsonDocument.Parse` on raw output.\n     - If JSON parse succeeds: iterate top-level properties; map each: `JsonValueKind.String` → `Text`; `JsonValueKind.Array` where all elements are strings → `List` (joined with `\"\\n\"`); everything else → `Json` (pretty-printed with `JsonSerializer.Serialize(elem, new JsonSerializerOptions { WriteIndented = true })`). Set `RawJson` to the pretty-printed root.\n     - If JSON parse fails: single `OutputField(\"Output\", Text, rawOutput)` with `RawJson = rawOutput`.\n\n3. **`src/VisualRelay.App/ViewModels/StageDetailViewModel.cs`** — MVVM ViewModel (headless-testable, no Avalonia types).\n   - Inherits `ViewModelBase`.\n   - `StageDetailState` enum: `{ NoStage, NotStarted, NotComplete, Ready, DriverStage }` — public.\n   - `[ObservableProperty]` members:\n     - `string _systemPromptText = \"\"`\n     - `IReadOnlyList<PromptSection> _inputSections = []`\n     - `IReadOnlyList<OutputField> _outputFields = []`\n     - `string _rawJson = \"\"`\n     - `string _header = \"\"`\n     - `StageDetailState _systemState = StageDetailState.NoStage`\n     - `StageDetailState _inputState = StageDetailState.NoStage`\n     - `StageDetailState _outputState = StageDetailState.NoStage`\n   - `void Load(StageRowViewModel? stage, string? taskDirectory)`:\n     - No stage or no taskDirectory or directory doesn't exist → all three `NoStage`, clear all content.\n     - Driver stage: look up `RelayStages.All.FirstOrDefault(s => s.Number == stage.Number)?.Kind`; if `\"driver\"` → all three `DriverStage`, set `Header` to `\"Stage {stage.Ordinal} ({stage.Name})\"`.\n     - Else:\n       - **System**: find latest `.input.json` via `StageInputArtifact.LatestPath(taskDirectory, stage.Number)`. If found, `TryRead` → `SystemPromptText = data.SystemPrompt`, `SystemState = Ready`. If no `.input.json` at all, fall back to `RelayStages.All[stage.Number - 1].SystemPrompt` → `Ready`. (System prompt is always available.)\n       - **Input**: same latest `.input.json`. If found and `TryRead` succeeds, `InputSections = AssembledPromptParser.Parse(data.InputPrompt)`, `InputState = Ready`. If no input file → `InputState = NotStarted`, empty sections.\n       - **Output**: find latest `.report.json` for the stage (same pattern: `stage{N}-attempt*.report.json`, pick max `RelayAttempt.AttemptNumber`). If found, read the file, parse JSON, extract `result.answer` string (fall back to empty). Run `OutputFieldParser.Parse(answer)`. Set `OutputFields`, `RawJson`, `OutputState = Ready`. If no report → `OutputState = NotComplete`.\n       - **Header**: `\"Stage {stage.Ordinal} ({stage.Name}) · attempt {attempt} · {size:0.#} KB\"` where size is `new FileInfo(inputPath).Length / 1024.0`, or `\"Stage {stage.Ordinal} ({stage.Name})\"` if no input file.\n\n4. **`src/VisualRelay.App/ViewModels/MainWindowViewModel.StageDetail.cs`** — wire-up partial.\n   - `StageDetailViewModel StageDetail { get; } = new();`\n   - At end of `SelectStage` (both select and clear paths): compute `taskDirectory = SelectedTask is { } task ? Path.Combine(RootPath, \".relay\", task.Id) : null`, call `StageDetail.Load(selectedStage, taskDirectory)` (selectedStage = the stage parameter on select, null on clear).\n   - In `HandleRelayEvent`: after the existing `ApplyStageEventToBoard(relayEvent)` call, add: if `relayEvent.EventName is \"stage_input\" or \"stage_done\"` and `relayEvent.StageNumber == _selectedStageFilter`, recompute `taskDirectory` from `SelectedTask?.Id` and call `StageDetail.Load(SelectedStageRow, taskDirectory)`.\n\n### Test files to create\n\n5. **`tests/VisualRelay.Tests/AssembledPromptParserTests.cs`** — pure unit tests, no filesystem.\n   - Tests: full prompt with all sections (every section present); minimal prompt (Task input + Prior stages + contract only); missing optional sections (no Task context, no Log sources, no Failing verify output, no Verify command); empty input → single \"Prompt\" section; null → empty; only header text (no `##` sections) → single \"Header\" section; \"Prior stages\" has `CollapsedByDefault = true`; \"Output contract\" extracted from trailing contract line; multiple blank lines handled.\n\n6. **`tests/VisualRelay.Tests/OutputFieldParserTests.cs`** — pure unit tests.\n   - Tests: Plan contract JSON `{\"plan\":\"...\", \"manifest\":[\"a\",\"b\"]}` → Text field + List field; Review contract `{\"verdict\":\"pass\", \"issues\":[]}` → Text + empty List; nested object → Json field; non-JSON blob → single Text field; null/empty → empty; fenced JSON extraction (output wrapped in ```json); pretty-printed RawJson matches input.\n\n7. **`tests/VisualRelay.Tests/StageDetailViewModelTests.cs`** — VM tests with filesystem isolation (TempDirectory or TestRepository).\n   - Tests: NoStage state (null stage, null taskDirectory, missing directory); DriverStage state (stage 11 Commit); Ready with persisted system prompt from .input.json; Ready with fallback system prompt (no .input.json); NotStarted input (no .input.json files); NotComplete output (no report files); Ready output (report with result.answer JSON); latest-attempt-by-number (write attempt 3 with older mtime, attempt 2 with newer mtime → picks attempt 3); Header format with size; live refresh via Load from SelectStage clear path; live refresh via Load from HandleRelayEvent with stage_input matching selected stage; stage_done for non-selected stage does not refresh.\n\n### Implementation order (TDD)\n1. Write `AssembledPromptParserTests` → red.\n2. Implement `AssembledPromptParser` → green.\n3. Write `OutputFieldParserTests` → red.\n4. Implement `OutputFieldParser` → green.\n5. Write `StageDetailViewModelTests` → red.\n6. Implement `StageDetailViewModel` → green.\n7. Implement `MainWindowViewModel.StageDetail.cs` wire-up → existing tests still green, new VM tests still green.\n8. Run `./visual-relay check` → green.\n9. Conventional Commit: `feat(app): stage-detail view model + assembled-prompt parser`",
  "manifest": [
    "+src/VisualRelay.App/Services/AssembledPromptParser.cs",
    "+src/VisualRelay.App/Services/OutputFieldParser.cs",
    "+src/VisualRelay.App/ViewModels/StageDetailViewModel.cs",
    "+src/VisualRelay.App/ViewModels/MainWindowViewModel.StageDetail.cs",
    "+tests/VisualRelay.Tests/AssembledPromptParserTests.cs",
    "+tests/VisualRelay.Tests/OutputFieldParserTests.cs",
    "+tests/VisualRelay.Tests/StageDetailViewModelTests.cs"
  ]
}

> **Already-resolved**: no implementation delta to strip; accepted green regression coverage.

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/AssembledPromptParserTests.cs",
    "tests/VisualRelay.Tests/OutputFieldParserTests.cs",
    "tests/VisualRelay.Tests/StageDetailViewModelTests.cs"
  ],
  "rationale": "Bottom-up TDD: pure parsers first with 26 unit tests covering full prompts, minimal prompts, missing optional sections, empty/null, fenced JSON extraction, non-JSON blobs, and all OutputFieldKind mappings. Then StageDetailViewModel with 12 filesystem-based tests covering all five StageDetailState transitions, latest-attempt-by-number (not mtime), persisted vs fallback system prompt, Header formatting, and load/clear lifecycle. Wire-up is minimal (2 call sites in SelectStage + HandleRelayEvent) with existing MainWindowViewModel tests still green. No Avalonia types in StageDetailViewModel — headless-testable as required."
}

## Stage 6 - Implement

{"summary": "All implementation files already exist and pass. AssembledPromptParser (108 lines), OutputFieldParser (97 lines), StageDetailViewModel (201 lines), and MainWindowViewModel.StageDetail.cs (25 lines) are complete. Wire-up via RefreshStageDetail already called from SelectStage and HandleRelayEvent. 38 new tests pass (12+12+14). Full suite: 1397 passed, 0 failed, 13 skipped. ./visual-relay check build succeeds; InspectCode exit 1 is pre-existing Roslyn infrastructure (System.Composition.AttributedModel load failure in sandboxed JB tool)."}

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{"summary": "Review verdict was 'pass' with zero issues. Fixed 3 inspect-code warnings on new files: removed 2 redundant System.IO. qualifiers in StageDetailViewModel.cs, documented the intentional empty catch in StageDetailViewModelTests.cs. All 38 targeted tests pass; full suite 1384 pass, 0 fail, 13 skipped. All 7 files under 300 lines. Verify command green."}

## Stage 9 - Verify

{
  "summary": "StageDetailViewModel + AssembledPromptParser + OutputFieldParser implemented and tested. All 38 tests pass, build is clean, files all under 300 lines. Wire-up in SelectStage and HandleRelayEvent is complete.",
  "commitMessages": [
    "feat(app): add StageDetailViewModel + assembled-prompt and output-field parsers",
    "feat(app): per-stage data layer with system/input/output tab sources and live refresh",
    "feat(app): stage-detail view model drives system-prompt, parsed input sections, and field-wise output"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

