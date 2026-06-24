# Completed non-Verify stage with no report shows the wrong "skipped because Verify passed" message

Follow-up to the `activity-missing-for-completed-stages` fix (commit `15841c4`). That change
correctly fixed the **Input** side (completed stages now show their input), but the **Output**
side still shows a factually wrong message in one case, and a new test locked that wrong
behavior in.

## Problem

`StageDetailViewModel.LoadOutput` maps a stage that is **Done but has no `.report.json`** to the
`Skipped` output state. The XAML for `Skipped` (`StageOutputView.axaml`) reads:

> "This stage was skipped because Verify passed with no issues."

That message is correct only for the genuine **Fix-verify-skipped** case (Verify passed, so
Fix-verify was skipped). For any **completed non-Verify stage** whose report is missing —
e.g. tasks committed before the artifact-persistence change, or a stage whose best-effort
report write failed — the Output tab now displays this **false** "Verify passed" text. This is
exactly the class of bug the original task set out to fix.

The fix already **added** a new `NotAvailable` output state and an `IsOutputNotAvailable`
placeholder border, but `LoadOutput` never routes to it — so that placeholder is currently
**unreachable** dead code. (Only the null-directory early-return sets Output to `NotAvailable`.)

A new test, `Load_DoneStageNoInputFile_InputNotAvailable`, asserts `OutputState == Skipped` for
a real directory with no report — **cementing the wrong behavior**; it must be corrected too.

## Fix

In `StageDetailViewModel.LoadOutput` (`src/VisualRelay.App/ViewModels/StageDetailViewModel.cs`):
when status is `Done` and no `.report.json` exists, set `OutputState = NotAvailable` for a
**non-Verify** stage (so the existing `IsOutputNotAvailable` placeholder — "Output for Stage NN
(…) is no longer available after the stage completed." — is shown), and reserve `Skipped`
strictly for the genuine Fix-verify-skipped case (key it on the stage's name/number, not merely
"Done + no report"). Update the `Load_DoneStageNoInputFile_InputNotAvailable` test to assert
`OutputState == NotAvailable` (not `Skipped`), and add a test that a real Fix-verify-skipped
stage still maps to `Skipped`.

## How to verify
- Select a completed **non-Verify** stage (e.g. Stage 02 Research) of a task that has **no**
  per-stage report on disk: the Output tab shows the "no longer available" placeholder, NOT
  "skipped because Verify passed".
- A genuinely Verify-passed → Fix-verify-skipped stage still shows the "skipped" message.
- Freshly-run stages still show real parsed Output content.

## Constraints
- VR is general-purpose; no project-specific assumptions. Files ≤300 lines (StageDetailViewModel.cs
  is already at 300 — relocate a helper rather than exceed it). `./visual-relay check` green;
  Conventional Commit subjects.
