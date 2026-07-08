# Obsidian Settings Row: Real Label for Poll Seconds, Matching Browse/Reveal Buttons

Two visual defects in the Obsidian-bridge row of the Settings dialog, both in
`src/VisualRelay.App/Views/Controls/ObsidianSettings.axaml` (introduced together by an earlier
automated change, commit `709f6d8`). The row is a
`Grid ColumnDefinitions="*,Auto,Auto,100"` holding: the vault-root `TextBox`, a Browse
`CommonButton`, a Reveal `CommonButton`, and a poll-seconds group.

## Defect 1 — the doubled "60"

The poll-seconds group renders as `60 [60]`: a hardcoded literal next to the editable value.

```xml
<TextBlock Text="60" FontSize="11" Foreground="#9AA3B1"
           VerticalAlignment="Center"/>
<TextBox Text="{Binding ObsidianPollSeconds, Mode=TwoWay}"
         FontSize="12" VerticalAlignment="Center" Width="50"/>
```

That `TextBlock` was clearly meant to be a **field label**, but it hardcodes the default value
instead — so the field reads as a mysterious doubled number, and if the user edits the value
(e.g. to 90) the row shows a stale `60 [90]`. What the field actually is (all verified):
`MainWindowViewModel.ObsidianBridge.cs` declares `_obsidianPollSeconds = 60`;
`OnObsidianPollSecondsChanged` clamps it to `ObsidianBridgeSettings.MinPollSeconds` (15);
it drives the vault poll timer (`DispatcherTimer { Interval = TimeSpan.FromSeconds(ObsidianPollSeconds) }`)
and persists through `ObsidianBridgeSettings`.

**Fix:** replace the literal with a real descriptive label naming the setting and its unit —
e.g. `Poll (seconds)` or equally concise wording. It must be static descriptive text, never a
number. A `ToolTip.Tip` on the group explaining "how often the vault is checked for new task
files (minimum 15)" is a cheap bonus. Layout: the group lives in the fixed-width `100` column;
a real label plus the 50px box will not fit in 100px — change that column to `Auto` (the
vault-root column is star-sized and absorbs the difference; keep everything on one line).

## Defect 2 — Browse and Reveal don't match

Both buttons already use the centralized component (`buttons:CommonButton` — see
`ButtonsCentralizationTests`, which forbids raw `<Button>` outside
`Views/Controls/Buttons/`), but they pick different appearance variants:

```xml
<buttons:CommonButton Grid.Column="1" Content="Browse"
                      Command="{Binding BrowseVaultRootCommand}"/>
<buttons:CommonButton Grid.Column="2" Content="Reveal"
                      Appearance="Path"
                      Command="{Binding RevealVaultRootCommand}"/>
```

`Appearance="Path"` maps to the `Button.path` style in `Styles/VisualRelayTheme.axaml`
(darker `#15181F` background, `#2A303A` border, fatter `14,9` padding) — the variant built for
buttons whose content **is a folder path** (the top bar's repo-path button). Reveal is a plain
row action, so it renders visibly darker and taller than its neighbor Browse.

**Fix:** remove `Appearance="Path"` from the Reveal button so both siblings use the `Default`
appearance (grey standard button, what Browse has today). Both buttons must end up visually
identical — same background, border, padding, height.

## Tests (extend the existing headless/VM patterns)

- Browse and Reveal in `ObsidianSettings` have equal `Appearance`, and it is
  `ButtonAppearance.Default` — construct the `ObsidianSettings` control directly in the
  headless collection (lightweight control-scoped precedent: `ChevronAffordanceRenderTests`);
  do not boot a full `MainWindow` for this.
- The poll-seconds label is non-numeric descriptive text (regression pin on the doubled-60
  bug: assert the label `TextBlock`'s text does not parse as an integer).
- The poll `TextBox` still two-way binds `ObsidianPollSeconds` (VM-level coverage exists in
  `ObsidianBridgeVmPropertiesTests` — keep it green; add the binding pin only if not already
  covered).
- `ButtonsCentralizationTests` still passes (no raw `<Button>` introduced).

## Done when

- The Settings row shows: vault-root box, two visually identical Browse/Reveal buttons, and a
  labeled single poll-seconds field — no doubled number anywhere, at any poll value.
- All tests above pass; full suite green; `./visual-relay check` passes.

## Guardrails

- XAML + tests only: no changes to the ViewModel, persistence, clamping, timer cadence, or the
  control API's `pollSeconds` exposure.
- Fix at the call site only — do not modify `CommonButton`, `ButtonControlThemes.axaml`, or
  `VisualRelayTheme.axaml`, and do not touch other `Appearance="Path"` usages (the top bar's
  repo-path button uses it legitimately).
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs; files stay
  under the 300-line guard.
