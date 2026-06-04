# No way to open a stage's raw trace / report artifacts from the UI

When a run misbehaves, the ground truth is in the on-disk artifacts: the report
(`.relay/<task>/stage*-attempt*.report.json`) and the Swival trace JSONL
(`.relay/<task>/stage*-attempt*/<session>.jsonl`). Today reaching them means leaving the app
for a terminal — painful in general and a blocker when driving the app remotely. The view model
already knows where they are: `StageRunMetric` carries `ReportPath` and `TraceDirectory`
(`src/VisualRelay.Domain/RunMetrics.cs:15-16`), populated in `RelayRunHistory.cs:86-100`.

There is no reveal/open affordance anywhere. The closest existing pattern is process launching
in `src/VisualRelay.Core/Execution/ProcessRunners.cs` and the folder picker in
`src/VisualRelay.App/Services/AvaloniaFolderPicker.cs`.

## Recommended fix

Add a "Reveal in Finder" affordance for the selected stage's artifacts:

- Add a command on the view model (e.g. `RevealStageArtifactsCommand`) that opens the selected
  stage's `TraceDirectory` (and/or selects its `ReportPath`) in the OS file manager.
- Implement a small cross-platform reveal helper: macOS `open -R <path>`, Windows
  `explorer /select,<path>`, Linux `xdg-open <dir>`. Avalonia's `TopLevel.Launcher`
  (`ILauncher.LaunchFileInfoAsync`) is a reasonable cross-platform option for opening the
  directory; the native reveal commands are the fallback when select-the-file behavior matters.
- Place the button where a failure is being inspected — the `RUN LOG` / `LLM COMMANDS` panel
  header (`src/VisualRelay.App/Views/Controls/ActivityColumn.axaml:16` / `:64`) and/or the
  detail-pane error surface (`surface-stage-error-in-detail-pane.md`).
- Disable/hide it when the selected stage has no `TraceDirectory`/`ReportPath` (no run yet).

## Sequencing

- **Land after `attempt-number-hardcoded-overwrites-reruns.md`.** Reveal targets a stage's latest
  attempt trace dir; before that fix every run shares `stage*-attempt1/` and the dir holds merged
  stale sessions, so "reveal latest trace" is ambiguous. With real per-run attempts it resolves
  to a clean single-session dir.
- If `surface-stage-error-in-detail-pane.md` has landed, attach the button to the error surface
  it adds (already listed here as a placement option).

## Done when

- With a stage that has run, the button opens the OS file manager at that stage's trace
  directory (or selects its report), on macOS at minimum.
- The button is disabled/hidden when there are no artifacts for the selection.
- The reveal helper is isolated enough to unit-test command construction per platform (no real
  process spawned in tests). Write the failing test first.
- Verify with `./visual-relay screenshot`.
- `./visual-relay check` green; files under 300 lines; Conventional Commit.
