# Regenerate the README screenshots from the current app

> Original ask: *"Update the screenshots in the readme based on the latest version of the app. The app
> has the ability to generate a screenshot of itself, so that'd be one way of generating it."*

The README's screenshot is stale relative to the current UI. Regenerate it (and the compact variant)
with the app's built-in self-screenshot tool, and make sure the demo state it renders still reflects
today's UI.

## Current state (researched — verify before editing)

- **README reference:** `README.md` line ~5: `![Visual Relay main window](docs/images/visual-relay-main.png)`.
  (Only the main image is referenced today.)
- **Image files:** `docs/images/visual-relay-main.png` and `docs/images/visual-relay-compact.png`.
- **Self-screenshot command:** `./visual-relay screenshot`
  (`tools/VisualRelay.Cli/Commands/ScreenshotCommand.cs`) renders **both**:
  - `docs/images/visual-relay-main.png` at **1440×900**
  - `docs/images/visual-relay-compact.png` at **1060×720**
  via the headless `tools/VisualRelay.Screenshots` project (`window.CaptureRenderedFrame()`).
- **Demo state:** the screenshot tool (`tools/VisualRelay.Screenshots/Program.cs`) builds a temporary
  demo project under `.relay-scratch/screenshot-root/` (a `.relay/config.json`, seeded `llm-tasks/`,
  and a view-model populated with demo tasks/stages/trace/log entries). **The screenshot only looks
  good if this seeded demo state still matches the current UI** — if UI elements were added/renamed,
  update the seeding so they appear populated, not empty.

> **Freshness contract.** Confirm the README image path(s), the `screenshot` subcommand, and the demo
> seeding by searching for `docs/images`, `ScreenshotCommand`, and `screenshot-root`; adapt if moved.

## Goal

`docs/images/visual-relay-main.png` (and `-compact.png`) reflect the current app UI, regenerated via
the built-in tool, with demo state that exercises the current UI (queue, task detail, stages, activity
column). README references are correct.

## Approach

1. Make sure `tools/VisualRelay.Screenshots/Program.cs`'s seeded demo state represents the current UI
   (recent changes — e.g. the 5-tab activity panel, stages board — should appear populated). Update the
   seeding if needed.
2. Run `./visual-relay screenshot` to regenerate both PNGs into `docs/images/`.
3. Visually verify the new images look right (this step needs a human/agent to *look* — see note), and
   confirm the README references them correctly (add the compact image to the README if it should be
   shown).

> ⚠️ **Visual correctness needs eyes.** A headless test can confirm the command runs and writes
> non-empty PNGs, but "the screenshot looks good / matches the current UI" is a visual judgment the
> automated pipeline can't make. Call this out; have a human (or an image-capable agent) eyeball the
> result before committing.

## Tests

- The `screenshot` command exits 0 and writes non-empty PNGs at the expected paths (a smoke test).
- If demo seeding changed, a unit test that the seeded view-model has the expected demo tasks/stages.

## Out of scope

- Reworking the screenshot tooling beyond demo-state freshness.
- Adding new images the README doesn't use.
