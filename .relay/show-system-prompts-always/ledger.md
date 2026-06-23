## Stage 1 - Ideate

{
  "summary": "When clicking a stage at startup (no tasks running), the System tab is empty because it reads the system prompt from a StageInputArtifact file (.input.json) that only gets written during task execution. The prompt is statically available via RelayStages.All[N].SystemPrompt, but the UI doesn't fall back to that source.",
  "options": [
    "A — UI fallback: When no artifact exists for the selected stage, display RelayStages.All[stageNumber].SystemPrompt directly in the System tab view model.",
    "B — Pre-seed artifacts: Write skeleton .input.json files for each stage at app startup so the existing artifact-read path always has data.",
    "C — Dedicated API: Expose a query method that returns the system prompt from RelayStages by stage number, decoupling the System tab from runtime artifacts entirely."
  ]
}

## Stage 2 - Research

{ "findings": "The bug is in StageDetailViewModel.Load() (src/VisualRelay.App/ViewModels/StageDetailViewModel.cs, line 99-103). When a stage is clicked at startup, SelectedTask is null, so taskDirectory resolves to null. The guard condition `string.IsNullOrEmpty(taskDirectory)` triggers early return with SetAllStates(NoStage) and ClearContent(), wiping SystemPromptText before LoadSystemPrompt() — which already has the correct static fallback to `RelayStages.All[N].SystemPrompt` — is ever called. The system prompt is statically defined in RelayStages.cs (line 43-98) per stage name, independent of any task artifact. The existing test Load_NullStageOrMissingDirectory_AllStatesNoStage (StageDetailViewModelTests.cs:18-29) codifies this broken behavior. The fix (Option A) is to separate the stage-null check from the directory-missing check: when a stage is provided but no task directory exists, call LoadSystemPrompt (which falls back to the static definition) and set Input/Output to NotStarted/NotComplete, rather than blanket-setting all three to NoStage.", "constraints": ["System prompt is static in RelayStages.All[stageNumber].SystemPrompt and does not depend on task artifacts.", "StageRowViewModel.Number is sufficient to look up the static definition in RelayStages.All.", "The guard must still handle stage==null (deselection) by clearing to NoStage.", "ClearContent() resets all detail fields — must keep input/output cleared when no task directory exists.", "LoadSystemPrompt() (lines 131-147) already implements correct static fallback; just needs to be reachable.", "Existing test Load_NullStageOrMissingDirectory_AllStatesNoStage asserts current broken behavior and must be updated.", "Driver stage (stage 11, Commit, Kind==\"driver\") is handled before the directory check and already works.", "SystemState must be Ready (not NoStage) to show prompt via XAML binding IsSystemReady.", "InputState/OutputState should be NotStarted/NotComplete (not NoStage) when no task directory exists."] }

## Stage 3 - Diagnose

{
  "evidence": "The bug is a control-flow error in StageDetailViewModel.Load() (line 97-104). The compound guard `if (stage is null || string.IsNullOrEmpty(taskDirectory) || !Directory.Exists(taskDirectory))` treats 'no stage selected' and 'no task directory exists' as the same case, calling SetAllStates(NoStage) + ClearContent() and returning early. At startup, SelectedTask is null, so RefreshStageDetail passes a valid stage but null taskDirectory. The guard fires on string.IsNullOrEmpty(null), blocking LoadSystemPrompt() — which already has a correct static fallback to RelayStages.All[N].SystemPrompt (line 142-143). The XAML binds SystemPromptText visibility to IsSystemReady, which requires SystemState == Ready; the guard leaves it at NoStage. The test Load_NullStageOrMissingDirectory_AllStatesNoStage codifies this broken behavior, while Load_NoInputFiles_FallbackSystemPrompt_InputNotStarted_OutputNotComplete proves the fallback works correctly when the guard isn't triggered.",
  "excerpts": [
    "StageDetailViewModel.Load() lines 97-104: compound guard `if (stage is null || string.IsNullOrEmpty(taskDirectory) || !Directory.Exists(taskDirectory))` returns early with SetAllStates(NoStage) + ClearContent(), preventing LoadSystemPrompt() from ever running when taskDirectory is null.",
    "LoadSystemPrompt() lines 131-147: already implements correct static fallback via `RelayStages.All.FirstOrDefault(s => s.Number == stage.Number)?.SystemPrompt`, but is unreachable when taskDirectory is null.",
    "MainWindowViewModel.RefreshStageDetail() (StageDetail.cs:17-24): passes `null` taskDirectory when `SelectedTask` is null (startup), triggering the faulty guard.",
    "RelayStages.SystemPromptFor() lines 43-98: static system prompts defined per stage name, always available without any task artifact.",
    "StageSystemView.axaml lines 19,47: prompt visibility gated on `IsSystemReady` (requires SystemState.Ready) which the guard never sets.",
    "Test Load_NullStageOrMissingDirectory_AllStatesNoStage (lines 17-29): asserts NoStage for all three states even when a valid stage is provided but directory is missing — codifies the broken behavior.",
    "Test Load_NoInputFiles_FallbackSystemPrompt_InputNotStarted_OutputNotComplete (lines 41-51): proves the fallback works correctly (SystemState.Ready with static prompt) when directory exists but has no files — confirming the guard is the sole defect."
  ],
  "repro": "Launch Visual Relay with no tasks running. Click any stage row (e.g., 'Ideate'). Observe the 'System' tab on the right panel: it shows the 'No stage selected' placeholder instead of the static system prompt. Expected: the static system prompt (e.g., 'Frame the task and list 2-3 solution options. Do not edit files.') should be displayed. Start a task, then click a stage that has run — the System tab works correctly, proving the prompt-display code is fine."
}

## Stage 4 - Plan

{
  "plan": "Split the compound early-return guard in StageDetailViewModel.Load() so that only `stage is null` triggers a full NoStage clear. When a stage is selected but taskDirectory is null/empty or the directory does not exist, call LoadSystemPrompt(null, stage) (static fallback), set InputState=NotStarted, OutputState=NotComplete, and build the header. Update the test to separately verify the null-stage and missing-directory cases.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/StageDetailViewModel.cs",
    "tests/VisualRelay.Tests/StageDetailViewModelTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": ["tests/VisualRelay.Tests/StageDetailViewModelTests.cs"],
  "rationale": "Split the old Load_NullStageOrMissingDirectory_AllStatesNoStage into three focused tests: (1) Load_NullStage_AllStatesNoStage asserts that passing null stage still clears everything to NoStage; (2) Load_ValidStageNullDirectory_ShowsStaticSystemPrompt_InputNotStarted_OutputNotComplete asserts that a valid stage with null taskDirectory sets SystemState=Ready with the static prompt, InputState=NotStarted, OutputState=NotComplete, and a header without attempt/size; (3) Load_ValidStageNonexistentDirectory_ShowsStaticSystemPrompt_InputNotStarted_OutputNotComplete asserts the same for a nonexistent directory. Tests (2) and (3) currently fail because the compound guard in Load() still treats missing directory as NoStage instead of falling through to LoadSystemPrompt's static-definition fallback."
}

## Stage 6 - Implement

{ "summary": "Split the compound early-return guard in StageDetailViewModel.Load() so only `stage is null` triggers a full NoStage clear. When a stage is selected but taskDirectory is null/empty or doesn't exist, the method now calls LoadSystemPrompt(null, stage) for the static fallback, sets InputState=NotStarted and OutputState=NotComplete, and builds the header without attempt/size. All 14 StageDetailViewModelTests pass." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "All 1857 tests passed (0 failed, 11 skipped) including all 14 StageDetailViewModelTests. The fix from Stage 6 is verified: when a stage is selected at startup with no task directory, StageDetailViewModel.Load() now calls LoadSystemPrompt() with the static fallback, sets InputState=NotStarted and OutputState=NotComplete, and builds the header — the System tab correctly shows the static system prompt." }

## Stage 9 - Verify

{
  "summary": "Fixed StageDetailViewModel.Load() so clicking a stage at startup (no task directory) displays the static system prompt from RelayStages.All instead of clearing to NoStage. The fix splits the old compound guard: null-stage still resets everything, but a valid stage with missing directory now calls LoadSystemPrompt with its static fallback, sets Input/Output to NotStarted/NotComplete, and builds a minimal header. All 14 StageDetailViewModelTests pass.",
  "commitMessages": [
    "fix(stage-detail): show static system prompt when stage selected before any task runs",
    "fix: split compound guard for null stage vs missing task directory in Load()",
    "fix: ensure System tab displays prompt without relying on task artifacts",
    "fix: decouple system prompt display from task directory existence"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

