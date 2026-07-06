# Show the Absolute-Ceiling Timeout in Minutes and Add a Quick "+10 min" Bump

When a stage hits the hard wall clock, the failure banner reads *"swival timed out after 1800000ms
absolute ceiling. Last signal: cpu, silence: 970ms."* — nobody thinks in raw milliseconds.
Reformat the ceiling duration as minutes/seconds at the source, and add a one-click remedy to the
failure banner: when the latest failure is an absolute-ceiling timeout, show a button that raises
the configured stage timeout by 10 minutes (so the user can bump and re-run without hand-editing
`.relay/config.json`).

## Current state (researched)

- **Message sites** — both in `src/VisualRelay.Core/Execution/ProcessRunners.RunAsync.cs`:
  - Watchdog path (`ActivityWatchdog.Outcome.FiredAbsoluteCeiling`):
    `$"swival timed out after {absoluteCeilingMs}ms absolute ceiling. Last signal:
    {wdResult.LastPulseSource}, silence: {wdResult.SilenceMs}ms."`
  - ProcessCapture-timeout backstop (`result.TimedOut`):
    `$"swival timed out after {absoluteCeilingMs}ms absolute ceiling. If swival was running a test
    command that hung, …"`
  - `absoluteCeilingMs` is the *effective* value (per-task 10× boost and escalation scaling
    already applied), so formatting it is sufficient — no config lookup needed.
- **Hint coupling** — `src/VisualRelay.Domain/ErrorHintClassifier.cs` appends the remedy hint by
  substring match: `Contains(rawError, "timed out")` → `TimeoutHint` (and `"test command timed
  out"` takes precedence). The literal phrase **"timed out" must survive** any reformatting.
  Mirror tests: `tests/VisualRelay.Tests/ErrorHintClassifierTests.cs`.
- **Existing assertions on the old format** — `rg -l "absolute ceiling"` in tests matches
  `SwivalSubagentRunnerWatchdogTests.ActivityWatchdog.cs`, `SwivalSubagentRunnerWatchdogTests.cs`,
  `SwivalSubagentRunnerWatchdogTests.TierWindows.cs`, `SwivalSubagentRunnerEscalationTests.cs`,
  and `ActivityWatchdogSocketWedgeTests*.cs` — update whichever assert the `…ms absolute ceiling`
  shape.
- **Formatting precedent** — `RelayDriver.Artifacts.cs` has a private
  `FormatDuration(double seconds)` producing `"2m 03s"`. It is private to `RelayDriver`; add a
  small sibling helper for milliseconds in `ProcessRunners.Helpers.cs` (225 lines, has headroom)
  rather than widening `RelayDriver`'s surface.
- **How the reason reaches the banner** — the flagged reason is written into the task's status
  record; `MainWindowViewModel.RunHistory.cs` derives `SelectedTaskError` from
  `LatestFlaggedError(statusRecord)`; `MainWindowViewModel.cs` exposes `HasSelectedTaskError`
  (`[NotifyPropertyChangedFor(nameof(HasSelectedTaskError))]` on `_selectedTaskError`).
- **Banner UI** — `src/VisualRelay.App/Views/Controls/TaskDetailPanel.axaml`: a `Border` with
  `IsVisible="{Binding HasSelectedTaskError}"` contains the "LATEST RUN FAILED" header and a
  `StackPanel` already hosting `buttons:CommonButton Content="{Binding CreateFixTaskButtonLabel}"
  Command="{Binding CreateFixTaskCommand}"` plus a busy `ProgressBar` — the new button belongs in
  that same `StackPanel`.
- **Button/command precedent** — `src/VisualRelay.App/ViewModels/MainWindowViewModel.FixTask.cs`:
  `[ObservableProperty]` state + `[RelayCommand(CanExecute = nameof(CanCreateFixTask))]` + a
  `Can*()` gating on `HasSelectedTaskError`/`IsBusy`. Mirror tests:
  `tests/VisualRelay.Tests/MainWindowViewModelFixTaskTests.cs` (+ `.Capabilities.cs`).
- **The configured ceiling** — `RelayConfig.SubagentTimeoutMilliseconds`, JSON key
  `"subagentTimeoutMs"` (`RelayConfigLoader.cs`: `OptionalInt(root, "subagentTimeoutMs", …)`).
  This repo's `.relay/config.json` currently sets `1800000` (30 min). Config is loaded at run
  start, so a bump applies **from the next run** — the button feedback must say so.
- **Config write precedent** — `src/VisualRelay.Core/Init/RelayConfigWriter.cs`:
  `UpsertCommitProofArtifacts(string rootPath, bool …)` read-modify-writes a single key while
  preserving all other keys. Writer tests: `tests/VisualRelay.Tests/RelayConfigWriterTests.cs`.

## What to build (TDD-first)

1. **Minutes formatting.** Add a helper in `ProcessRunners.Helpers.cs` that renders milliseconds
   as `"30m 00s"` (`"90s"`-style is unnecessary; ceilings are minutes-scale). Rewrite both
   messages to the shape: `swival timed out after 30m 00s (1800000 ms) absolute ceiling. …` —
   human-first, raw ms preserved in parentheses, and the phrases **"timed out"** and **"absolute
   ceiling"** kept verbatim (hint classifier + banner detection below both key on them). Tests
   first: helper unit tests plus updating the existing watchdog/escalation test assertions; add an
   `ErrorHintClassifierTests` case proving the reformatted message still yields `TimeoutHint`.
   Leave the `silence: {…}ms` fragment as-is (sub-second precision is meaningful there).

2. **"+10 minutes" bump button.** New partial
   `src/VisualRelay.App/ViewModels/MainWindowViewModel.CeilingBump.cs`:
   - `IsCeilingTimeoutError` — true when `SelectedTaskError` contains `"absolute ceiling"`
     (OrdinalIgnoreCase). Raise its change notification from the generated
     `partial void OnSelectedTaskErrorChanged(...)` hook implemented **in this new partial** — do
     not add attributes to `_selectedTaskError` in `MainWindowViewModel.cs` (that file is at the
     300-line ceiling).
   - `BumpCeilingCommand` (`[RelayCommand(CanExecute = …)]`, gated on `IsCeilingTimeoutError`,
     `!IsBusy`, repo initialized): load current config, write `subagentTimeoutMs + 600_000` via
     `RelayConfigWriter.UpsertSubagentTimeout(string rootPath, int milliseconds)` — add it
     (mirroring `UpsertCommitProofArtifacts`; preserve every other key) or reuse it if an earlier
     change already introduced it — then set `StatusText` to e.g.
     `"Stage timeout raised to 40m — applies from the next run."`.
   - Button label, e.g. `"Increase timeout by 10 min"`.
   Tests first: writer tests (`UpsertSubagentTimeout_*`: sets value, preserves keys, creates key
   when absent) and view-model tests mirroring the FixTask ones (visible only for ceiling errors,
   disabled while busy, bump persists and updates status text).

3. **Wire the button** into the banner `StackPanel` in `TaskDetailPanel.axaml` next to the
   fix-task button, `IsVisible="{Binding IsCeilingTimeoutError}"`.

## Done when

- Both kill messages render the ceiling as `Xm YYs (…ms)` and the timeout hint still appends.
- A task whose latest failure mentions the absolute ceiling shows the bump button; clicking it
  rewrites `subagentTimeoutMs` (+600000, other keys byte-preserved), and the status bar confirms
  the new value and that it applies from the next run. Non-ceiling failures show no button.
- `./visual-relay check` passes (file-size guard, format verification, build, full test suite,
  README screenshot render).

## Guardrails

- Conventional Commits only (the `commit-msg` hook enforces the full ruleset). See
  `docs/commit-messages.md` and `AGENTS.md`.
- 300-line ceiling (`tools/VisualRelay.Guards`): **`MainWindowViewModel.cs` is at 300** — do not
  add a single line there (use the partial-method hook described above);
  `ProcessRunners.RunAsync.cs` is at 290 and `TaskDetailPanel.axaml` at 293 — keep net additions
  within headroom (the format helper lives in `ProcessRunners.Helpers.cs`, the VM logic in the new
  partial).
- Do not change watchdog semantics, `HardAbort` handling, or the timeout *values* — this task is
  presentation plus a config write.
- No control-API surface for the bump button — UI-only is in scope; automation parity is not.
- Headless UI tests use `[AvaloniaFact]`/`[AvaloniaTheory]`; plain logic tests use xUnit `[Fact]`
  with the `TestRepository` helper, matching the existing FixTask/writer tests.
- Minimal diffs: change only what this task needs; do not reformat or reflow unrelated code.
