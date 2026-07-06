# Make Stage and Test Timeouts Editable in Settings

The two big per-run time caps — the per-stage absolute wall-clock ceiling (`subagentTimeoutMs`,
currently 1800000 ms = 30 min in this repo's `.relay/config.json`) and the test-command cap
(`testTimeoutMs`, currently 1200000 ms = 20 min) — can only be changed by hand-editing
`.relay/config.json`. Surface both in the Settings dialog as minute-denominated numeric fields so
adjusting default timeouts is a two-click operation.

## Current state (researched)

- **Config model** — `src/VisualRelay.Domain/RelayConfig.cs`: record fields
  `int SubagentTimeoutMilliseconds` (comment: "Hard absolute wall-clock ceiling per stage
  invocation (ms) … Scaled by 10× for tasks in BoostTurnsTaskIds. Set to 0 to disable (not
  recommended).") and `int TestTimeoutMilliseconds`.
- **Loader** — `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` already parses both:
  `SubagentTimeoutMilliseconds = OptionalInt(root, "subagentTimeoutMs", …)` and
  `TestTimeoutMilliseconds = OptionalInt(root, "testTimeoutMs", …)`. No loader work needed.
- **Writer precedent** — `src/VisualRelay.Core/Init/RelayConfigWriter.cs`:
  `UpsertCommitProofArtifacts(string rootPath, bool …)` read-modify-writes one JSON key while
  preserving all other keys (224 lines; has headroom). Writer tests:
  `tests/VisualRelay.Tests/RelayConfigWriterTests.cs`.
- **Settings view-model pattern** — `src/VisualRelay.App/ViewModels/MainWindowViewModel.Settings.cs`
  (106 lines): repo-scoped settings are `[ObservableProperty]` fields whose
  `partial void On<X>Changed(...)` persists via `RelayConfigWriter.Upsert*` guarded by
  `Directory.Exists(RootPath)` (see `OnCommitProofArtifactsChanged`). Hydrate the new fields from
  config wherever `CommitProofArtifacts` is hydrated today (alongside the config load that also
  feeds `HydrateTurnBudget` in `MainWindowViewModel.Helpers.cs`). Mirror tests:
  `tests/VisualRelay.Tests/MainWindowViewModelSettingsTests.cs`.
- **Settings UI** — `src/VisualRelay.App/Views/SettingsWindow.axaml` hosts
  `src/VisualRelay.App/Views/Controls/SettingsPanel.axaml` (**297 lines — effectively at the
  300-line ceiling**). Field precedent: labeled `TextBox Grid.Column="1"` rows; section precedent:
  the `CommitProofCheckBox` block and the `Expander Header="Sandbox Paths"`. There is an existing
  precedent for splitting settings UI into its own control:
  `src/VisualRelay.App/Views/Controls/ObsidianSettings.axaml`.
- **Semantics to convey in the UI** — config is loaded at run start, so edits apply from the next
  run; the stage timeout is multiplied ×10 for tasks with the "10× turn budget" boost
  (`RelayDriver.Invocation.cs` applies `SaturatingBoost` to `SubagentTimeoutMilliseconds` for
  boosted task ids); `0` disables the ceiling entirely (allowed by the engine, discouraged).

## What to build (TDD-first)

1. **Writer methods.** Add (or reuse, if an earlier change already introduced one)
   `RelayConfigWriter.UpsertSubagentTimeout(string rootPath, int milliseconds)` and
   `UpsertTestTimeout(string rootPath, int milliseconds)`, byte-preserving every other key —
   mirror `UpsertCommitProofArtifacts`. Tests first in `RelayConfigWriterTests.cs`: sets the
   value, creates the key when absent, preserves all other keys, round-trips through
   `RelayConfigLoader`.

2. **View model.** In `MainWindowViewModel.Settings.cs` add `StageTimeoutMinutes` and
   `TestTimeoutMinutes` (`int`, `[ObservableProperty]`):
   - Hydrate from config: `Math.Round(ms / 60000.0)` clamped to the valid range (display-only
     rounding; do not write back on load).
   - On change: clamp to **1–720 minutes**, persist `minutes * 60000` via the writer, and revert
     the property to the clamped value when input was out of range. Persist only on actual user
     change, mirroring the `OnCommitProofArtifactsChanged` guard.
   - Do not expose a 0/disable path in the UI; hand-editing config keeps that escape hatch.
   Tests first in `MainWindowViewModelSettingsTests.cs`: hydrates from config, clamps, persists
   the converted ms value, ignores no-op sets.

3. **Settings UI.** Create a new `src/VisualRelay.App/Views/Controls/TimeoutSettings.axaml`
   user control (precedent: `ObsidianSettings.axaml`) with a small "Timeouts" section: two
   labeled numeric fields — "Stage timeout (minutes)" and "Test timeout (minutes)" — bound to the
   new properties, each with a tooltip noting: applies from the next run; the stage timeout is
   ×10 for tasks with the 10× turn-budget boost. Reference the control from `SettingsPanel.axaml`
   with a one-line include so the panel stays at or under 300 lines.

## Done when

- Settings shows the two timeout fields in minutes with current values; edits clamp to 1–720,
  persist to `.relay/config.json` as `subagentTimeoutMs`/`testTimeoutMs` (all other keys
  preserved), survive app restart, and take effect on the next run.
- `SettingsPanel.axaml` and every touched C#/XAML file remain ≤ 300 lines.
- `./visual-relay check` passes (file-size guard, format verification, build, full test suite,
  README screenshot render).

## Guardrails

- Conventional Commits only (the `commit-msg` hook enforces the full ruleset). See
  `docs/commit-messages.md` and `AGENTS.md`.
- 300-line ceiling (`tools/VisualRelay.Guards`): **`SettingsPanel.axaml` is at 297** — the new
  section must live in the new `TimeoutSettings.axaml` control, not inline.
- Config stays millisecond-denominated (`subagentTimeoutMs`/`testTimeoutMs`) — minutes are a UI
  affordance only; do not rename or re-unit config keys.
- Per-tier `firstOutputTimeoutMsByTier` / `inactivityTimeoutMsByTier` maps are **out of scope** —
  only the two scalar timeouts get UI.
- Headless UI tests use `[AvaloniaFact]`/`[AvaloniaTheory]`; plain logic tests use xUnit `[Fact]`
  with the `TestRepository` helper, matching the existing settings tests.
- Minimal diffs: change only what this task needs; do not reformat or reflow unrelated code.
