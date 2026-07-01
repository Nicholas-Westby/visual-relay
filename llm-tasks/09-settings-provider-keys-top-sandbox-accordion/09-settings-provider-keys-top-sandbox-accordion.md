# Reorganize Settings: Provider Keys to the Top, Sandbox Paths to the Bottom in a Collapsed Accordion

The Settings screen currently **leads with a long list of sandbox paths** and pushes the Provider
Keys — the most useful, most-edited settings — far down the scroll (in the screenshot the "Provider
Keys" header is only just visible at the very bottom). Flip that priority: put **Provider Keys near
the top**, and move the long **Sandbox Paths** lists to the **very bottom inside an accordion that is
collapsed by default**, so they stay available but out of the way.

See `Screenshot-settings.png` in this folder for the current (inverted) ordering.

## Current state

**The screen.** `src/VisualRelay.App/Views/SettingsWindow.axaml` (`SettingsWindow`) is just a frame +
Close button that hosts `<controls:SettingsPanel/>`. The actual content is
`src/VisualRelay.App/Views/Controls/SettingsPanel.axaml` (`SettingsPanel`, a `UserControl` with
`x:DataType="vm:MainWindowViewModel"`; its `.axaml.cs` is trivial `InitializeComponent()`). It is one
`<ScrollViewer x:Name="SettingsScrollViewer">` wrapping a `<StackPanel Spacing="6">` whose **children,
top → bottom, are the sections** — ordering is purely their position in this `StackPanel`, so
reordering = moving the child blocks:

1. Header `<Grid>`: `Text="Settings"` title + `RevealSettingsFileButton` ("Show in Finder").
2. `<Border>` with `CommitProofCheckBox` — "Commit Visual Relay's run proof to this repo".
3. `<Border>` with `VerboseDiagnosticsCheckBox` — "Verbose sandbox diagnostics" (bound to
   `VerboseSandboxDiagnostics`).
4. `<!-- Sandbox paths (read-only, derived from the enforced profile at runtime) -->` →
   `<controls:SandboxPaths/>` — the Writable / Readable / Blocked lists (this is the long one).
5. `<!-- Provider keys section -->` → a `TextBlock Text="Provider Keys"` header, then **five
   per-provider `<Border>` rows** each anchored by a comment (`<!-- HF row (index 0) -->`,
   `<!-- DeepSeek row (index 1) -->`, `<!-- Moonshot row (index 2) -->`, `<!-- Anthropic row (index 3) -->`,
   `<!-- OpenAI row (index 4) -->`), then the `HfPricingNote` line. Each row binds **positionally** to
   `KeyStates[i]` (e.g. `Text="{Binding KeyStates[0].Row.DisplayName}"`); a code comment notes this is
   deliberate so `FindControl` can locate named controls without crossing a `DataTemplate` boundary.
6. `<!-- Lit-tiers summary -->` → `<Border>` with `Text="Live Tiers"` + an
   `<ItemsControl x:Name="LitTierItems" ItemsSource="{Binding LitTierRows}">`.
7. `<!-- Obsidian bridge settings -->` → `<controls:ObsidianSettings/>`.

Stable anchors for the move are the XML comments above and the header strings, **not** line numbers.

**Accordion idiom already used in this app.** Avalonia's built-in `<Expander>` is the house pattern —
no new control is needed:

- `src/VisualRelay.App/Views/Controls/StageOutputView.axaml`: `<Expander Header="{Binding Label}" IsExpanded="True" ...>`
- `src/VisualRelay.App/Views/Controls/StageInputView.axaml`: a **collapsed-by-default** example, binding
  `IsExpanded` through a NOT converter — `IsExpanded="{Binding CollapsedByDefault, Converter={x:Static cvt:BoolNotConverter.Instance}}"`.

`BoolNotConverter` (with `public static readonly BoolNotConverter Instance`) lives at
`src/VisualRelay.App/Views/Controls/BoolNotConverter.cs`. For a **statically** collapsed section you
don't even need the converter — `<Expander Header="…" IsExpanded="False">` is enough.

**`SandboxPaths` is self-contained.** `src/VisualRelay.App/Views/Controls/SandboxPaths.axaml`
(`SandboxPaths`) already gates its own visibility on `IsSandboxInfoLoading` / `IsSandboxInfoAvailable`
and manages its three `ItemsControl`s internally, so it can be wrapped in an `Expander` as-is.

## What to build

1. **Reorder the `StackPanel` children in `SettingsPanel.axaml`** to this target order (top → bottom):
   - Settings header (unchanged, stays first).
   - **Provider Keys** section (the `Provider Keys` header + all five rows **in their existing order** +
     `HfPricingNote`) — moved up to be the first content section.
   - **Live Tiers** — keep it adjacent to Provider Keys (it's model/key related).
   - "Commit Visual Relay's run proof" checkbox.
   - Obsidian bridge settings.
   - **Sandbox section** (bottom) — see item 2.

   The exact order of the *middle* sections (Live Tiers / Commit-proof / Obsidian) is a matter of good
   taste; the firm requirements are only that **Provider Keys is the first content section** and the
   **sandbox section is last and collapsed**. When moving the Provider Keys block, keep its inner markup
   byte-for-byte — in particular the five rows must stay in the same order and keep their positional
   `KeyStates[0..4]` bindings and `<!-- … (index N) -->` comments aligned.

2. **Move the sandbox content to the bottom and collapse it by default.**
   - Wrap `<controls:SandboxPaths/>` in an `<Expander Header="Sandbox Paths" IsExpanded="False">`
     (collapsed on open, expandable on click) and place that Expander as the **last** section.
   - The **"Verbose sandbox diagnostics" toggle is sandbox-related** — move it down to live with the
     sandbox section (just above the Expander, or as the Expander's first child) so all sandbox controls
     sit together at the bottom. Use good taste for exact placement.
   - Match the existing bordered-section styling and the `StackPanel Spacing="6"` rhythm. Give the
     Expander a clear header ("Sandbox Paths" or "Sandbox filesystem policy").

3. **Layout-only change.** No ViewModel / command / logic changes; do not alter what any section shows
   or does. Bindings, commands, and `SandboxPaths`' internal behavior stay exactly as-is.

## Constraints & done criteria

- On opening Settings, **Provider Keys is the first content section** below the header, and the
  **Sandbox Paths section is the last section and is collapsed** (`IsExpanded="False"`); clicking it
  expands the Writable / Readable / Blocked lists unchanged.
- **No behavior change:** the five provider rows keep their order, positional `KeyStates[0..4]`
  bindings, and index comments; Live Tiers, Commit-proof, Obsidian, and the sandbox lists all still work
  and bind to the same state.
- Reuse the existing Avalonia `<Expander>` idiom (as in `StageInputView.axaml`); **do not** add a new
  custom accordion control.
- Respect the repo's **≤300-line** per-file gate. `SettingsPanel.axaml` is already long; if the reorder
  pushes it over, factor the Provider Keys block into its own control
  (`src/VisualRelay.App/Views/Controls/ProviderKeysSettings.axaml` + `.axaml.cs`), mirroring the
  existing `ObsidianSettings` control, and embed it — rather than inflating `SettingsPanel.axaml`.
- Full `Verify` gate green (`Failed: 0`, exit 0).

## Files likely in scope (the plan stage finalizes the manifest)

- `src/VisualRelay.App/Views/Controls/SettingsPanel.axaml` — reorder sections; wrap `SandboxPaths` in a
  collapsed `Expander` at the bottom; relocate the "Verbose sandbox diagnostics" toggle to the sandbox
  section.
- (only if the 300-line gate requires) `src/VisualRelay.App/Views/Controls/ProviderKeysSettings.axaml`
  (+ `.axaml.cs`) — extract the Provider Keys block into its own control.
- (reference, no change) `src/VisualRelay.App/Views/Controls/SandboxPaths.axaml`,
  `src/VisualRelay.App/Views/Controls/StageInputView.axaml` (the collapsed-`Expander` pattern),
  `src/VisualRelay.App/Views/Controls/BoolNotConverter.cs`.
