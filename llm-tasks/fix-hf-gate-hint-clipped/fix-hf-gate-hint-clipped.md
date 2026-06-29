# No-keys setup banner: the HF-gate hint "or open Settings ⚙ in the top bar" is clipped

On first run with **no API keys configured**, the QueuePanel shows the Hugging-Face "gate"
banner pinned at its bottom (the no-keys call-to-action). Its second line — a **"Get a free
token →"** button followed inline by the hint **"or open Settings ⚙ in the top bar"** — is cut
off at the panel's right edge. It renders as **"…Get a free token →  or open Settings ⚙"** with
**"in the top bar" missing** (the gear is at the very edge, the trailing words are gone).

This is the same family of fixed-width clipping bug already fixed for the init card's buttons in
`init-panel-buttons-labels-clipped` — a different element in the **same** `QueuePanel`.

## Problem

The banner only appears in the no-keys state (`ShowHfGate` = key states loaded once **and**
`HF_TOKEN` absent), so it is exactly the screen a first-time / no-keys user sees. The hint is
laid out **horizontally next to the button** in a non-wrapping `TextBlock`, and the button +
spacing + hint together exceed the QueuePanel's fixed width, so the panel's clip cuts the hint.

## Evidence

- **Screenshots** (committed beside this task, captured from a real headless Skia render of
  `MainWindow` at 1440×900 with no keys, `XDG_CONFIG_HOME` redirected to an empty dir so no real
  key file was read):
  - `llm-tasks/fix-hf-gate-hint-clipped/hf-gate-clipped-crop.png` — close-up of the banner; the
    bottom line reads `Get a free token →  or open Settings ⚙` and stops there.
  - `llm-tasks/fix-hf-gate-hint-clipped/first-run-no-keys.png` — the whole app at the real 280 px
    panel width (init card + the clipped gate banner at bottom-left).
- **The clipped element** is the inline hint `TextBlock` in
  `src/VisualRelay.App/Views/Controls/QueuePanel.axaml`, inside the
  `IsVisible="{Binding ShowHfGate}"` border:

  ```xml
  <StackPanel Orientation="Horizontal" Spacing="8">
    <Button Command="{Binding OpenGetKeyUrlCommand}"
            CommandParameter="https://huggingface.co/settings/tokens"
            Padding="10,4" MinHeight="28"
            Content="Get a free token →"/>
    <TextBlock Text="or open Settings ⚙ in the top bar"
               FontSize="11" Foreground="#8E96A3"
               VerticalAlignment="Center"/>   <!-- ← this is clipped -->
  </StackPanel>
  ```

## Facts established by measuring (bake in — do not re-derive)

Measured in a headless render (Inter font; see caveat) of the no-keys `MainWindow`:

- **The QueuePanel is a fixed 280 px wide** — `src/VisualRelay.App/Views/MainWindow.axaml`:
  `<controls:QueuePanel Width="280" …/>`. **This width is intentional — do NOT widen the panel
  to "solve" the clipping** (same constraint as the sibling task).
- The gate `Border` has `Padding="14"`, so the inner content is ~250 px wide (the horizontal
  `StackPanel` arranged to **250 px**).
- The **"Get a free token →"** button arranges to **146 px**; with `Spacing="8"` that leaves only
  ~96 px for the hint, but the hint's unconstrained desired width is **172 px**. Its right edge
  wants **341 px** inside a 280 px panel → ~**61 px (≈35%)** of the hint is cut by the panel's
  outer `<Border Classes="panel" ClipToBounds="True">`. Avalonia `TextBlock` does not wrap or
  trim by default, so a horizontal `StackPanel` next to a button overflows and is clipped.
- **Caveat (font metrics):** the screenshot tool and the headless test harness both use the
  embedded **Inter** font (`WithInterFont()`), while the real desktop uses the system default
  font, so absolute pixel widths differ slightly between environments. The ~76 px shortfall is
  large enough that the hint clips under any reasonable UI font at this fixed 280 px width.

## What to do

At the panel's fixed **280 px** width, make the **entire** gate hint visible — robust to longer /
localized text, not tuned to today's exact string. No behaviour change to the button (keep its
`Command`, the `huggingface.co/settings/tokens` URL, and the `Get a free token →` label).

Recommended (Plan/Implement may refine): **stop laying the hint out horizontally beside the
button.** Put the **"or open Settings ⚙ in the top bar"** `TextBlock` on its own line below the
button (the gate's outer container is already a vertical `StackPanel`) and give it
`TextWrapping="Wrap"` so it uses the full ~250 px content width and wraps instead of clipping.
This mirrors the accepted fix in `init-panel-buttons-labels-clipped` (stack vertically rather than
cram side-by-side at 280 px).

Rejected alternatives (do not re-litigate): widening the QueuePanel (280 px is intentional);
shrinking font/padding to keep button + hint on one line (fragile, still clips on longer/localized
strings); trimming the hint with an ellipsis (hides the actionable instruction).

## Tests (TDD — write the failing test first)

Headless-UI, in the style of `tests/VisualRelay.Tests/InitPanelButtonsLayoutTests.cs` /
`ConfigInitEmptyStateUiTests.cs`. Force the no-keys gate on: build the VM with an
`EnvironmentAccessor` seeded so `HF_TOKEN` is absent (see `KeySetupPanelUiTests` /
`SettingsTestHelpers.SeedUserEnv(repo, "")`), `await LoadInitialAsync()` so `_keyStatesLoaded`
flips and `ShowHfGate == true`, then `Show()` the `MainWindow`.

- Resolve the hint `TextBlock` (e.g. by its text starting with "or open Settings") and assert it is
  **not clipped** at the fixed 280 px panel width — e.g. its arranged `Bounds.Width` ≥ its
  unconstrained `DesiredSize.Width`, **or** that it wraps (its arranged right edge, translated into
  the `QueuePanel`, is ≤ the panel width). This fails on today's horizontal layout (right edge
  ~341 px > 280 px) and passes once the hint stacks/wraps.
- Keep `KeySetupPanelUiTests`, `ConfigInitEmptyStateUiTests`, and `InitPanelButtonsLayoutTests`
  green.

## Out of scope

- The init card's button labels (already fixed in `init-panel-buttons-labels-clipped`).
- The top-bar **FOLDER** path ellipsis (intentional `TextTrimming`, not a bug).
- The SettingsPanel key-setup screen — it renders fully and scrolls (a `ScrollViewer`) at short
  heights; no clipping there.

## Done when

- At the default window size and the fixed 280 px QueuePanel width, the **whole** HF-gate hint
  ("or open Settings ⚙ in the top bar") is visible — nothing clipped — and robust to longer labels.
- The "Get a free token →" button is unchanged (label, command, URL) and still works.
- A headless-UI test asserts the hint no longer clips (fails before, passes after); the existing
  settings/init UI tests stay green.
- A re-screenshot / headless render of the no-keys `MainWindow` confirms the banner no longer clips.
- `./visual-relay check` is green. Conventional Commit.
