# Use Component Composition for Buttons — and Consolidate Their API Into a Theme

Two goals, and the second is the one this task must not lose sight of:

1. **Composition over inheritance.** The three custom button components — `CommonButton`,
   `IconButton`, `StageCardButton` (all in `src/VisualRelay.App/Views/Controls/Buttons/`) —
   currently subclass Avalonia `Button`. They must instead **contain** a `Button`. No class in the
   app may inherit from `Button`, and the `NoClassInheritsFromButton` guard test must stop
   grandfathering these three classes.
2. **Consolidation.** The entire reason these custom components exist is to be the app's button
   *theme*: a small, closed set of appearances and behaviors, defined once. The composed versions
   must have **fewer** knobs than today's inherited versions, not a faithful re-plumbing of every
   `Button` property. **Just because a call site currently sets some property on a custom button
   does not mean the composed component must expose that property.** When the refactor hits a
   consumer that uses something outside the closed API below, the fix is to change the consumer
   (fold the one-off into a variant, or drop it) — not to widen the component.

## Design rule: the theme owns appearance

- Everything visual — padding, heights, min-widths, font sizes, colors, hover states — is defined
  per **variant** in the control theme, in one place. Call sites pick a variant; they do not
  restyle instances. Per-instance `Width="…"`, `Height="…"`, `FontSize="…"`, `Padding="…"`,
  `MinHeight="…"` attributes on custom-button tags in consumer XAML are exactly the drift this
  task exists to remove: migrate each one into the appropriate variant's theme definition (or
  delete it if the themed default already serves), and only then, if two-plus call sites genuinely
  need a distinct look, consider whether that *is* a missing variant.
- The variant set is the existing `ButtonAppearance` enum: Primary / Default / Warning / Hyperlink
  / Path, plus the icon-button styles (CollapseToggle / FocusToggle) and the stage-card style. Do
  not add new variants for single call sites, and do not add "escape hatch" properties (no
  `InnerButtonStyle`, no pass-through `Classes`, etc.).
- It is expected — not a regression — that a few buttons shift by a couple of pixels when their
  hand-tuned per-instance metrics are replaced by the variant's canonical metrics. Normalizing
  that drift is the point. Visual parity matters at the variant level (a Primary button looks like
  a Primary button), not at the level of preserving every instance's quirks.

## Closed public API (the whole surface, after this task)

- `CommonButton`: `Content`, `Command`, `CommandParameter`, `Click` (routed event; XAML handlers
  in `SettingsWindow.axaml`, `TopBar.axaml`, `StageOutputView.axaml`, `StageInputView.axaml` use
  it), `Flyout` (used by `QueuePanel.axaml`), `Appearance`, `Glyph`.
- `IconButton`: `IconStyle`, `ChevronDirection`, `IsContracted`, `Command`.
- `StageCardButton`: `Content`, `Command`, `CommandParameter`.

Nothing else gets a styled property or is forwarded to the inner button. Base-class members
(`IsVisible`, `IsEnabled`, alignment, attached layout props like `Grid.Column`/`DockPanel.Dock`,
`ToolTip.Tip`) exist inherently on any control and stay usable for *layout*; the line is that
consumers must not use base-class members to *restyle* button visuals per instance.

## Current state (researched — inventory, not a compatibility contract)

- All three components subclass `Button` with `protected override Type StyleKeyOverride =>
  typeof(Button);`. `CommonButton` adds `Appearance`/`Glyph` styled props and applies classes
  (`primary`/`warning`/`hyperlink`/`path`); `IconButton` adds `IconStyle`/`ChevronDirection`/
  `IsContracted` and sets chevron/focus icon content; `StageCardButton` adds the `stageButton`
  class.
- Theme styles in `src/VisualRelay.App/Styles/VisualRelayTheme.axaml` select on `Button` +
  class (`Button.primary`, `Button.warning`, `Button.stageButton`, `Button.path`,
  `Button.hyperlink`, `Button.collapseToggle`, `Button.focusToggle`); included from `App.axaml`.
- Guard tests in `tests/VisualRelay.Tests/ButtonsCentralizationTests.cs`:
  `NoClassInheritsFromButton` (currently grandfathers the Buttons dir via
  `IsInButtonsDirectory` — remove that), `NoRawButtonTags_InAxaml_OutsideButtonsDirectory` and
  `NoNewButtonExpressions_InCs_OutsideButtonsDirectory` (stay in force: raw `<Button>` /
  `new Button` may only live inside `Views/Controls/Buttons/`, plus the existing `App.axaml`
  exemptions).
- Known per-instance styling drift to clean up while migrating consumers: XAML call sites set
  `Padding`, `FontSize`, `MinHeight`, `Width`, `Height` on custom buttons in several views, and
  `App.axaml.cs::CreateConfirmButton` hand-builds its buttons in code
  (`MinWidth=80, Padding=new Thickness(12,0), Height=32, …`) — those metrics move into the
  variant (a Primary/Default dialog button should get its size from the theme).
- `tests/VisualRelay.Tests/ConfirmationDialogButtonAlignmentTests.cs` currently pins
  instance-level plumbing (reads `Padding`/`Height`/`MinWidth`/alignments off the `CommonButton`
  instance). **These tests encode the old design and should be revised**: assert that the confirm
  dialog's buttons are `CommonButton`s with the right variant and that the *rendered/themed*
  button carries the correct metrics — however the theme delivers them — rather than requiring
  per-instance property assignment.

## What to build

1. **Red first.** Remove the `IsInButtonsDirectory` grandfather from `NoClassInheritsFromButton`
   (update its doc/assert message: no class under `src/VisualRelay.App` may inherit from
   `Button`). This fails against the current components and drives the refactor.
2. **Compose.** Re-base the three components on a non-`Button` base (`ContentControl` or
   `TemplatedControl`) whose `ControlTheme` template hosts a single inner
   `<Button Name="PART_Button">`. Forward only the closed API above: variant → classes on the
   inner button (so the existing `Button.<class>` theme selectors keep matching), content/glyph
   composition to the inner button's `Content`, `Command`/`CommandParameter`/`Flyout` to the inner
   button, and re-raise the inner button's `Click` as the component's routed `Click` event
   (`CommonButton` only). Do **not** template-bind a laundry list of layout/styling props into the
   inner button; the theme supplies them per variant.
3. **ControlThemes live in the Buttons dir.** The `<Button>` template markup must be in a new
   `.axaml` under `src/VisualRelay.App/Views/Controls/Buttons/` (the raw-tag guard requires it);
   include it from `App.axaml`. Variant metrics (the confirm-dialog sizes, any migrated
   per-instance values) are defined here / in `VisualRelayTheme.axaml` — once.
4. **Migrate consumers to the theme.** Sweep every XAML/code use of the three components: remove
   per-instance visual attributes (fold needed values into the variant), switch
   `CreateConfirmButton` to plain variant-picking construction, and leave layout-only usage
   (grid placement, visibility, tooltips) alone. Consumers are expected to change in this task.
5. **Keep the theme honest (recommended, small).** Extend `ButtonsCentralizationTests` with a
   scan asserting consumer XAML sets no visual-styling attributes (`Width`, `Height`, `FontSize`,
   `Padding`, `MinHeight`, `MinWidth`) on `CommonButton`/`IconButton`/`StageCardButton` tags —
   the guard that keeps the surface from re-ballooning after this task.
6. **Verify.** `./visual-relay check` (guards, format, build, tests, screenshot render); use the
   running app's control API (`GET /screenshot`) to eyeball each variant, the icon toggles, and
   stage cards.

## Done when

- `NoClassInheritsFromButton` passes with no grandfather; nothing under `src/VisualRelay.App`
  inherits `Button`; the raw-tag / `new Button` guards still pass.
- The three components expose exactly the closed API above — no additional styled properties, no
  pass-through styling hooks — and render through an inner themed `Button`.
- Consumer XAML/code contains no per-instance visual styling of custom buttons; variant metrics
  (including the confirm dialog's) live in the theme.
- `ConfirmationDialogButtonAlignmentTests` is revised per above and green;
  `ButtonsCentralizationTests` (including the new consumer scan, if added) and `ContrastTests`
  are green; `./visual-relay check` passes.

## Guardrails

- Do not preserve old surface for its own sake; do not add variants or properties to appease a
  single call site — change the call site.
- 300-line ceiling per file (`tools/VisualRelay.Guards`); split the new ControlTheme axaml from
  component code as needed.
- Conventional Commits (`docs/commit-messages.md`, `AGENTS.md`); minimal diffs outside the
  Buttons dir, the consumer sweep, the theme files, and the named tests.
