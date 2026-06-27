# Polish FocusToggleIcon geometry + tighten its render test

Follow-up from the code review of `fix-funky-expand-collapse-icons` (commit `6935126`).
Minor — cosmetic geometry + test precision. The icon is correct and standard.

## Current state (researched)
`src/VisualRelay.App/Views/Controls/FocusToggleIcon.cs` now draws diagonal fullscreen arrows.
Review found two nits:
1. In the CONTRACT state, the arrowhead barbs reach `mid + Gap + Head = 8.0 + 1.3 + 4.0 = 13.3`,
   slightly outside the `hi = IconSize - Inset = 13.0` box the EXPAND state respects. Imperceptible
   at 16px / 1.7px stroke, but a latent asymmetry if the icon is ever scaled up.
2. `tests/VisualRelay.Tests/ChevronAffordanceRenderTests.cs` `FocusIcon_Renders_NonEmptyInkInsetFromEdges`
   uses `minMargin = 1.5`; the OLD corner-elbow design (Inset 2.5) would have PASSED it, so it does
   not actually anchor the old defect. The `FocusIcon_Expand_And_Contract_AreDistinctShapes` test IS
   the genuine regression guard.

## What to build
1. Adjust the contract barbs so all ink stays within `[Inset, IconSize-Inset]` in BOTH states
   (e.g. set `Head = hi - mid - Gap`, or reduce `Gap`), keeping the arrows visually balanced and
   still clearly "exit fullscreen". Re-verify the expand state is unchanged/symmetric.
2. Either tighten the inset-ink test so it would FAIL the old edge-to-edge design (make it a real
   anchor) or remove/correct the overstated comment, leaving the distinct-shapes test as the guard.
3. `./visual-relay check` green.

## Decisions (settled)
- Cosmetic + test precision only; do not change the icon's overall design (diagonal arrows stays).
