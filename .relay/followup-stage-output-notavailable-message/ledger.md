## Stage 1 - Ideate

{
  "summary": "In StageDetailViewModel.LoadOutput, when status is Done and no .report.json exists, route to NotAvailable for non-Verify stages (reserving Skipped for Fix-verify stage 10 only). Fix the Load_DoneStageNoInputFile_InputNotAvailable test assertion from Skipped to NotAvailable. The existing Load_StageDoneNoReport_OutputStateSkipped test already covers the Fix-verify→Skipped path and will continue passing.",
  "options": [
    "Option A — Nested ternary: change line 212 from `? StageDetailState.Skipped` to `? stageNumber == 10 ? StageDetailState.Skipped : StageDetailState.NotAvailable`. Zero line-count increase, no relocation needed. Fix the test assertion. ~3-line total diff.",
    "Option B — If/else with helper relocation: replace the ternary with an if/else block, move `LatestReportPath` (22 lines) to a `Services/StageOutputHelper.cs` to stay ≤300 lines. Cleaner flow, more lines changed.",
    "Option C — Name-based check with signature change: pass `RelayStageDefinition` to `LoadOutput` and check `definition.Name == \"Fix-verify\"`. Most explicit but modifies the method signature and caller. Over-engineered for this change."
  ]
}

## Stage 2 - Research

{
  "findings": "## Files and their roles\n\n### 1. `src/VisualRelay.App/ViewModels/StageDetailViewModel.cs` (300 lines)\n- **`StageDetailState` enum** (line 9): Defines `NoStage`, `NotStarted`, `NotComplete`, `Ready`, `DriverStage`, `Skipped`, `NotAvailable` — all needed states already exist.\n- **`IsOutputNotAvailable`** (line 92): Boolean property binding the XAML placeholder — already wired.\n- **`LoadOutput(string taskDirectory, int stageNumber, string? status)`** (lines 204–243): The bug lives here. Lines 211–213 unconditionally set `OutputState = StageDetailState.Skipped` when status is `Done` and no `.report.json` exists, regardless of which stage it is. Lines 217–242 handle the happy path (report exists → parse → `Ready`).\n- **`LatestReportPath`** (lines 245–266): A 22-line static helper that enumerates `stage{N}-attempt*.report.json` and picks the highest attempt number.\n- **`Load(StageRowViewModel? stage, string? taskDirectory)`** (lines 114–163): Calls `LoadOutput` at line 159, passing `stage.Number` and `stage.Status`. The stage number is available and already passed.\n- **The early return** (lines 133–148): Already correctly uses `NotAvailable` for Done+null/missing directory (lines 141, 144).\n- **Constraint**: File is exactly 300 lines (line 300 is `}`). Adding lines requires relocating a helper (e.g. moving `LatestReportPath` to a new file) or using a zero-line-increase approach like a nested ternary.\n\n### 2. `src/VisualRelay.App/Views/Controls/StageOutputView.axaml`\n- **`NotAvailable` placeholder** (lines 84–95): Already present and correctly wired to `IsOutputNotAvailable`. Message: \"Output for {0} is no longer available after the stage completed.\" — This is the correct message for non-Verify completed stages with missing reports.\n- **`Skipped` placeholder** (lines 71–82): Shows \"This stage was skipped because Verify passed with no issues.\" — Correct only for Fix-verify (stage 10). Currently reachable for **all** Done+no-report stages due to the bug.\n- **No changes needed** in this file.\n\n### 3. `src/VisualRelay.App/Views/Controls/StageInputView.axaml`\n- Has its own `NotAvailable` placeholder (lines 71–82) for the Input side, already correct.\n\n### 4. `tests/VisualRelay.Tests/StageDetailViewModelTests.cs` (293 lines)\n- **`Load_DoneStageNoInputFile_InputNotAvailable`** (lines 233–245): Uses Stage 3 (Diagnose) with `Status=\"Done\"` and no report. Currently asserts `OutputState == Skipped` on line 244 — this is the **wrong assertion** that must change to `NotAvailable`. The test name focuses on Input, but it implicitly validates Output too; the Output assertion (line 244) must be updated.\n- **`Load_StageDoneNoReport_OutputStateSkipped`** (lines 193–207): Uses Stage 10 (Fix-verify) with `Status=\"Done\"`. Asserts `OutputState == Skipped` on line 202 — this is **correct** and must continue passing (it covers the genuine Fix-verify-skipped case).\n- **`Load_StageNotDoneNoReport_OutputStateNotComplete`** (lines 208–218): Regression guard — Stage 10 (Fix-verify) with `Status=\"Waiting\"` → OutputState is `NotComplete`, not `Skipped`.\n- **Helper `MakeStage(int n, string name, string tier)`** (lines 11–13): Creates a `StageRowViewModel` from stage number, name, and tier. Stage 10 → `MakeStage(10, \"Fix-verify\", \"balanced\")`.\n\n### 5. `src/VisualRelay.Core/Execution/RelayStages.cs` (108 lines)\n- Stage 10 is `\"Fix-verify\"` (line 22). Stage 9 is `\"Verify\"` (line 21). Stage 11 (index 10) is `\"Commit\"` (driver stage, line 23).\n- The only stage that can legitimately be \"skipped because Verify passed\" is stage 10 (Fix-verify).\n- Stage ordering is: 1 Ideate, 2 Research, 3 Diagnose, 4 Plan, 5 Author-tests, 6 Implement, 7 Review, 8 Fix, 9 Verify, 10 Fix-verify, 11 Commit.\n\n### 6. The bug\n`LoadOutput` (line 211–212) uses a flat ternary that treats **any** Done+no-report stage as `Skipped`. The `NotAvailable` state already exists in the enum, the ViewModel property, and the XAML, but `LoadOutput` never sets it. The only code path that sets `Output = NotAvailable` is the early-return (line 144) for null/missing directories.\n\n### 7. The fix\nChange lines 211–213 so that when status is `Done` and no report exists, the output state is `NotAvailable` **unless** `stageNumber == 10` (Fix-verify), in which case it remains `Skipped`. The `stageNumber` parameter is already available in `LoadOutput`. Update the test at line 244 to assert `NotAvailable` instead of `Skipped`.",
  "constraints": [
    "File length limit: `StageDetailViewModel.cs` is exactly 300 lines. Any solution that adds lines must relocate a helper (e.g. `LatestReportPath`, 22 lines) to a new file under `Services/` (e.g. `StageOutputHelper.cs`) to stay ≤300 lines.",
    "Conventional Commit subjects required for all changes.",
    "`./visual-relay check` must pass green after changes.",
    "VR is general-purpose: no project-specific assumptions (e.g. no hardcoding of task IDs or paths). The stage-number-10 check is acceptable because it mirrors the existing number-based pattern at line 124 (`definition.Number == 11` for the driver stage).",
    "The existing test `Load_StageDoneNoReport_OutputStateSkipped` (stage 10, Fix-verify) must continue to pass with `OutputState == Skipped`.",
    "The test `Load_DoneStageNoInputFile_InputNotAvailable` must change its Output assertion from `Skipped` to `NotAvailable` (line 244).",
    "The `NotAvailable` placeholder in `StageOutputView.axaml` (lines 84–95) and the `IsOutputNotAvailable` property (line 92) already exist — no XAML or property changes needed.",
    "Option A (nested ternary) keeps the diff minimal (~3 lines: change line 212 + fix test assertion) and avoids file-length increase. Option B (if/else + helper relocation) is cleaner but adds more lines changed."
  ]
}

## Stage 3 - Diagnose

{
  "evidence": "The bug is in StageDetailViewModel.LoadOutput (StageDetailViewModel.cs, lines 211–213). When status is \"Done\" and LatestReportPath returns null (no .report.json on disk), the code unconditionally sets OutputState = StageDetailState.Skipped — regardless of which stage it is. The Skipped placeholder in StageOutputView.axaml (lines 71–82) reads 'This stage was skipped because Verify passed with no issues.' That message is only correct for stage 10 (Fix-verify), the sole stage that is genuinely skipped when Verify passes. For any other completed stage whose report is missing (e.g., Stage 3 Diagnose, or a task committed before artifact persistence was added in commit 15841c4, or a best-effort report write that failed), the Output tab shows this factually wrong 'Verify passed' text. The enum StageDetailState already includes NotAvailable (line 9), the IsOutputNotAvailable boolean property exists (line 92), and the XAML NotAvailable placeholder is already wired to it (lines 84–95) with the correct message: 'Output for {0} is no longer available after the stage completed.' But LoadOutput never routes to NotAvailable — the only code path that sets Output = NotAvailable is the early return for null/missing directories (lines 133–148), making the NotAvailable output state unreachable dead code for the normal Done+no-report case. The test Load_DoneStageNoInputFile_InputNotAvailable (line 244) asserts OutputState == Skipped for Stage 3 (Diagnose), cementing the wrong behavior. Meanwhile, Load_StageDoneNoReport_OutputStateSkipped (lines 193–207) correctly uses Stage 10 (Fix-verify) and expects Skipped — that test must continue to pass. The stageNumber parameter is already available in LoadOutput (line 204). RelayStages.cs (line 22) confirms Stage 10 is 'Fix-verify', the only stage that can legitimately be skipped.",
  "excerpts": [
    "StageDetailViewModel.cs:211-213: `OutputState = \"Done\".Equals(status, StringComparison.OrdinalIgnoreCase) ? StageDetailState.Skipped : StageDetailState.NotComplete;` — unconditionally maps any Done+no-report stage to Skipped, regardless of stageNumber.",
    "StageDetailViewModel.cs:9: `public enum StageDetailState { NoStage, NotStarted, NotComplete, Ready, DriverStage, Skipped, NotAvailable }` — NotAvailable already exists in the enum but LoadOutput never routes to it.",
    "StageDetailViewModel.cs:92: `public bool IsOutputNotAvailable => OutputState == StageDetailState.NotAvailable;` — property already wired but unreachable from LoadOutput.",
    "StageOutputView.axaml:71-82: Skipped placeholder — 'This stage was skipped because Verify passed with no issues.' — factually wrong for non-Verify stages.",
    "StageOutputView.axaml:84-95: NotAvailable placeholder — 'Output for {0} is no longer available after the stage completed.' — correct message, already wired to IsOutputNotAvailable.",
    "RelayStages.cs:22: `Stage(10, \"Fix-verify\", ...)` — Stage 10 is the only stage that can legitimately be skipped because Verify passed.",
    "StageDetailViewModelTests.cs:244: `Assert.Equal(StageDetailState.Skipped, vm.OutputState);` — asserts Skipped for Stage 3 (Diagnose), locking in the wrong behavior.",
    "StageDetailViewModelTests.cs:193-207: `Load_StageDoneNoReport_OutputStateSkipped` uses Stage 10 (Fix-verify) and correctly expects Skipped — this test must continue passing."
  ],
  "repro": "1. Create a TempDirectory with no artifacts. 2. Construct a StageRowViewModel for Stage 3 (Diagnose) with Status=\"Done\". 3. Call vm.Load(stage, dir.Path). 4. Observe vm.OutputState == StageDetailState.Skipped — the Output tab shows 'This stage was skipped because Verify passed with no issues' even though Stage 3 is Diagnose, not Fix-verify. Expected: OutputState == StageDetailState.NotAvailable, showing 'Output for Stage 03 (Diagnose) is no longer available after the stage completed.'"
}

## Stage 4 - Plan

{
  "plan": "## Fix\n\nIn `StageDetailViewModel.LoadOutput` (line 212), the unconditional `? StageDetailState.Skipped` is changed to `? stageNumber == 10 ? StageDetailState.Skipped : StageDetailState.NotAvailable`. This routes Done stages with no report to `NotAvailable` (for the existing placeholder) unless the stage is 10 (Fix-verify), which remains `Skipped`. Zero line-count increase — file stays at 300 lines. The `stageNumber` parameter is already available (line 204), and this mirrors the existing number-based pattern at line 124.\n\nIn `StageDetailViewModelTests.cs` (line 244), the assertion `OutputState == Skipped` for Stage 3 (Diagnose) is changed to `OutputState == NotAvailable` with an added `IsOutputNotAvailable` check, matching the pattern of the existing `Load_StageDoneNoReport_OutputStateSkipped` test.\n\nThe existing `Load_StageDoneNoReport_OutputStateSkipped` test (Stage 10, Fix-verify) continues to pass unchanged, covering the genuine Fix-verify → Skipped case.",
  "manifest": [
    "src/VisualRelay.App/ViewModels/StageDetailViewModel.cs",
    "tests/VisualRelay.Tests/StageDetailViewModelTests.cs"
  ]
}

## Stage 5 - Author-tests

{
  "testFiles": [
    "tests/VisualRelay.Tests/StageDetailViewModelTests.cs"
  ],
  "rationale": "Two test changes encode the target behavior: (1) Load_DoneStageNoInputFile_InputNotAvailable now asserts OutputState == NotAvailable (not Skipped) for a non-Verify Done stage (Stage 3 Diagnose) with no report, plus IsOutputNotAvailable/!IsOutputSkipped boolean checks. (2) New test Load_DoneNonVerifyStageNoReport_OutputStateNotAvailable provides a dedicated focused assertion of the same rule — a Done non-Verify stage with no report maps to NotAvailable (not Skipped). Both fail before implementation because LoadOutput unconditionally sets Skipped for any Done+no-report stage. The existing Load_StageDoneNoReport_OutputStateSkipped (Stage 10 Fix-verify → Skipped) continues to pass, confirming the genuine Fix-verify-skip path is preserved."
}

## Stage 6 - Implement

{ "summary": "Fixed StageDetailViewModel.LoadOutput (line 212) to route Done+no-report non-Verify stages to `NotAvailable` instead of `Skipped` — only stage 10 (Fix-verify) retains `Skipped`. The fix uses a nested ternary (`stageNumber == 10 ? Skipped : NotAvailable`) keeping the file at exactly 300 lines. Moved the private `TempDirectory` class from `StageDetailViewModelTests.cs` (309→297 lines) to the existing `TestFileSystem.cs` to satisfy the 300-line guard. All 1946 tests pass, 0 failures." }

## Stage 7 - Review

{ "verdict": "pass", "issues": [] }

## Stage 8 - Fix

{ "summary": "The fix from the Implement stage is already applied and verified correct. StageDetailViewModel.LoadOutput (line 212) now routes non-Verify Done+no-report stages to NotAvailable (showing 'no longer available' placeholder) and reserves Skipped (showing 'skipped because Verify passed') strictly for Stage 10 Fix-verify. The TempDirectory helper was relocated to TestFileSystem.cs to keep StageDetailViewModelTests.cs at 297 lines. All 31 StageDetailViewModel tests pass." }

## Stage 9 - Verify

{
  "summary": "In StageDetailViewModel.LoadOutput, changed the unconditional ternary that mapped any Done+no-report stage to Skipped into a nested ternary that routes to NotAvailable for non-Verify stages (showing the correct \"no longer available\" placeholder), reserving Skipped strictly for stage 10 (Fix-verify). Fixed the Load_DoneStageNoInputFile_InputNotAvailable test assertion from Skipped to NotAvailable and added Load_DoneNonVerifyStageNoReport_OutputStateNotAvailable. All 31 tests pass; build green; file lengths at 300/297 lines.",
  "commitMessages": [
    "fix(output): route Done+no-report non-Verify stages to NotAvailable instead of Skipped",
    "fix(output): reserve \"skipped because Verify passed\" message for Fix-verify only",
    "fix: correct LoadOutput state for completed stages with missing report files"
  ]
}

## Stage 10 - Fix-verify

_Skipped: Verify passed; nothing to fix._

## Stage 11 - Commit

Committed by Visual Relay.

