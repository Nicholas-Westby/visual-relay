# Harden the settings modal open path

Follow-up from the code review of `fix-settings-panel` (commit `e011ce1`). Defensive only —
the modal works correctly today; these guard against future regressions.

## Current state (researched)
`src/VisualRelay.App/Views/Controls/TopBar.axaml.cs` `OnSettingsClick` (~lines 19-42) resolves
the owner via `TopLevel.GetTopLevel(this) as Window` and calls `ShowDialog(owner)`; there is an
`else dialog.Show()` non-modal fallback for the owner-null case. Two minor gaps the review found:
1. No `if (vm.IsSettingsOpen) return;` duplicate-open guard. Safe in production because the modal
   `ShowDialog` blocks input to the owner (cog can't be re-clicked), but not explicit.
2. The `else dialog.Show()` branch is unreachable in this desktop app (owner is always a `Window`);
   if it ever ran it would open a NON-modal settings window with no input-blocking and no duplicate
   guard — the one place gap #1 would actually bite.

## What to build
1. Add `if (vm.IsSettingsOpen) return;` (or equivalent) as the first line of `OnSettingsClick`, so a
   duplicate settings window can never be opened regardless of modality. Ensure `IsSettingsOpen` is
   set true on open and reset false on close (verify the close path already resets it; if relying on
   `ShowDialog`'s completion, confirm `CloseSettings`/the window-closed handler resets it).
2. Either remove the unreachable `else dialog.Show()` branch, or keep it but pair it with the
   duplicate guard + a one-line comment that owner-null only happens off-desktop. Pick the cleaner.
3. `./visual-relay check` green. Add/extend a headless test if practical (e.g. calling the open path
   twice yields a single owned settings window).

## Decisions (settled)
- Defensive/robustness only; do not change the modal's appearance or settings behavior.
