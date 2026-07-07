# Use Component Composition for Buttons

The three custom button components — `CommonButton`, `IconButton`, `StageCardButton` — currently
subclass Avalonia `Button`. They must instead **contain** a `Button` (composition, not inheritance):
no class in the app may inherit from `Button`. This keeps the components from inheriting button
behaviors they don't want, while still rendering a real themed `Button` internally. The
`NoClassInheritsFromButton` guard must stop grandfathering these three classes.

## Current state (researched)

All three live in `src/VisualRelay.App/Views/Controls/Buttons/` and subclass `Button` with
`protected override Type StyleKeyOverride => typeof(Button);`:

- `CommonButton.cs` — `public partial class CommonButton : Button`. Styled props `AppearanceProperty`
  (enum `ButtonAppearance`: Primary/Default/Warning/Hyperlink/Path) and `GlyphProperty`.
  `ApplyAppearance` does `Classes.Add("primary"|"warning"|"hyperlink"|"path")`; `ApplyGlyph` wraps
  `Content` in a `StackPanel` (glyph `TextBlock` + content) using `_originalContent`/`_isWrapping`
  and an `OnPropertyChanged` override on `ContentProperty`.
- `IconButton.cs` — `public partial class IconButton : Button`. Props `IconStyleProperty`
  (enum `IconButtonStyle`: CollapseToggle/FocusToggle), `ChevronDirectionProperty`,
  `IsContractedProperty`. `ApplyIconStyle` does `Classes.Add("collapseToggle"|"focusToggle")` and
  sets `Content` to a `ChevronIcon` or `FocusToggleIcon` bound to direction/`IsContracted`.
- `StageCardButton.cs` — `public partial class StageCardButton : Button`; ctor does
  `Classes.Add("stageButton")`.

Theme styles in `src/VisualRelay.App/Styles/VisualRelayTheme.axaml` key off `Button` instances
carrying a class: `Button.primary`, `Button.warning`, `Button.stageButton`, `Button.path`,
`Button.hyperlink`, `Button.collapseToggle`, `Button.focusToggle`. That file is included from
`App.axaml` via `<StyleInclude Source="avares://VisualRelay.App/Styles/VisualRelayTheme.axaml"/>`.

XAML consumer surface that **must keep working unchanged** (do not edit these consumers):

- `CommonButton`: `Content`, `Command`, `CommandParameter`, `Appearance`, `Glyph`; the `Click`
  routed event (`SettingsWindow.axaml` `Click="OnCloseClick"`, `TopBar.axaml`
  `Click="OnSettingsClick"`, `StageOutputView.axaml`/`StageInputView.axaml` `Click="Copy…"`); and
  the `Flyout` property (`QueuePanel.axaml` `<buttons:CommonButton.Flyout>`). Plus standard
  Control/ContentControl layout props set in XAML (`Padding`, `FontSize`, `MinHeight`, `Width`,
  `Height`, `H/VAlignment`, `H/VContentAlignment`, `IsVisible`, `ToolTip.Tip`, `Grid.Column`,
  `DockPanel.Dock`, `x:Name`).
- `IconButton`: `IconStyle`, `ChevronDirection`, `IsContracted`, `Command`, layout props.
- `StageCardButton`: `Command`, `CommandParameter`, rich `Content` (a `Border`) inside a
  `DataTemplate` (`StageBoard.axaml`), `Width`.

Only `App.axaml.cs::CreateConfirmButton` constructs these in code: `new CommonButton { Content=…,
Appearance=ButtonAppearance.Primary, MinWidth=80, Padding=new Thickness(12,0), Height=32,
HorizontalContentAlignment=Center, VerticalContentAlignment=Center }` (plus a Cancel
`new CommonButton { Content="Cancel", … }`). No code-behind reads `.Appearance`/`.IconStyle`/etc.
on instances.

Existing tests that constrain the design (`tests/VisualRelay.Tests/`):

- `ButtonsCentralizationTests.cs::NoClassInheritsFromButton` — scans all `*.cs` under
  `src/VisualRelay.App` for regex `\bclass\s+\w+\s*:\s*Button\b`, but **skips** the Buttons dir via
  `IsInButtonsDirectory(file)` (the grandfather to remove). Sibling tests
  `NoRawButtonTags_InAxaml_OutsideButtonsDirectory` (regex `<Button(?:\s|)>`, exempts the Buttons
  dir + `App.axaml`) and `NoNewButtonExpressions_InCs_OutsideButtonsDirectory` (regex
  `new\s+Button`, exempts the Buttons dir + `App.axaml.cs`) stay in force — so any `<Button>` tag in
  XAML must live inside `Views/Controls/Buttons/`, and any `new Button(…)` must live there too.
- `ConfirmationDialogButtonAlignmentTests.cs` (`[Collection("Headless")]`, `[AvaloniaFact]`):
  `ConfirmButton_VerticalContentAlignment_IsCenter` reads `button.Content`, `Padding`, `Height`,
  `MinWidth`, `HorizontalContentAlignment`, `VerticalContentAlignment` directly off the
  `CommonButton` instance; `ConfirmButton_IsCommonButton_WithPrimaryAppearance` asserts
  `GetType().Name == "CommonButton"` and `Appearance == 0`. These pin the component to a base that
  still exposes `Content` + `Vertical/HorizontalContentAlignment`.

## What to build (decided approach — final)

Base class: each component subclasses **`ContentControl`** (not `Button`, not `TemplatedControl`).
`ContentControl` preserves `Content` and `Vertical/HorizontalContentAlignment` so the
`ConfirmationDialogButtonAlignmentTests` assertions still hold; the component's own `Content` stays
the user's label/rich content (so `button.Content` reads correctly) while a **real inner `Button`**
renders it.

1. **Red first.** In `ButtonsCentralizationTests.cs::NoClassInheritsFromButton`, remove the
   `IsInButtonsDirectory(file)` `continue` grandfather (and update the method XML doc + the
   `Assert.True` message to state that **no** class under `src/VisualRelay.App` may inherit from
   `Button`). This now fails against the still-`Button`-derived components — that is the failing
   test driving the refactor.

2. **Convert the three components** to `ContentControl` with a `ControlTheme` whose template is a
   single inner `<Button Name="PART_Button">`. Remove `StyleKeyOverride => typeof(Button)` from all
   three (the default `StyleKey` is the concrete type, matching the new `ControlTheme` selector).

   - Grab the inner button in `OnApplyTemplate` (`e.NameScope.Find<Button>("PART_Button")`) and
     store it; re-sync all forwarded state there and whenever the relevant properties change.
   - **Theme classes go on the inner `Button`**, not the component, so `Button.primary` /
     `Button.collapseToggle` / `Button.stageButton` etc. still match: `CommonButton.ApplyAppearance`
     → `_button.Classes`; `IconButton.ApplyIconStyle` → `_button.Classes`;
     `StageCardButton` ctor → `_button.Classes.Add("stageButton")`.
   - **Inner-button `Content` is assigned in code** (do not `{TemplateBinding Content}` it):
     `CommonButton` reuses its existing glyph-wrap logic but targets `_button.Content` instead of
     `this.Content` (keep the `_originalContent`/`_isWrapping`/`OnPropertyChanged` machinery,
     retargeted); `IconButton` sets `_button.Content` to the `ChevronIcon`/`FocusToggleIcon`
     (preserve the existing `Bind` to `ChevronDirection`/`IsContracted`); `StageCardButton` passes
     its `Content` straight through to `_button.Content`.
   - Forward `Command` and `CommandParameter` to `_button` (set on the inner button on apply /
     change). Template-bind the layout/styling props that affect the inner button's look
     (`Padding`, `FontSize`, `MinHeight`, `Width`, `Height`, `H/VAlignment`,
     `H/VContentAlignment`, `IsVisible`) via `{TemplateBinding …}` in the template so XAML-set
     values take effect on the inner button.
   - **`CommonButton` only:** declare a bubbling `Click` routed event
     (`RoutedEvent.Register<CommonButton, RoutedEventArgs>`) + `Click` event accessor (the XAML
     `Click="…"` handlers expect `RoutedEventArgs`), and raise it by subscribing to the inner
     button's `Click`. Declare a `Flyout` `StyledProperty<FlyoutBase?>` and forward it to
     `_button.Flyout` so `QueuePanel.axaml`'s `<buttons:CommonButton.Flyout>` keeps working.

3. **ControlThemes file.** Put the three `ControlTheme`s (selectors `controls|CommonButton`,
   `controls|IconButton`, `controls|StageCardButton`, with the `controls:` namespace already used
   in `VisualRelayTheme.axaml`) in a **new** `.axaml` under
   `src/VisualRelay.App/Views/Controls/Buttons/` — the `<Button>` template tags must be inside the
   Buttons directory to satisfy `NoRawButtonTags_InAxaml_OutsideButtonsDirectory`. Include it from
   `App.axaml` alongside the existing `VisualRelayTheme.axaml` `StyleInclude`. Keep each source file
   under the 300-line guard.

4. **Verify.** Run `./visual-relay check` (file-size guard, format, build, tests, screenshot
   render). Use the running app's control API (`GET /screenshot`) to confirm visual parity for the
   primary/warning/hyperlink/path variants, the collapse/focus icon toggles, and the stage cards.

## Done when

- `NoClassInheritsFromButton` has **no** grandfather (scans the Buttons dir too) and passes; no
  `*.cs` under `src/VisualRelay.App` matches `class … : Button`.
- `CommonButton`, `IconButton`, `StageCardButton` subclass `ContentControl`, each hosting an inner
  `Button` (the `new Button` / `<Button>` live inside `Views/Controls/Buttons/`, so
  `NoNewButtonExpressions_InCs_OutsideButtonsDirectory` and
  `NoRawButtonTags_InAxaml_OutsideButtonsDirectory` still pass).
- `ConfirmationDialogButtonAlignmentTests` (both tests), all of `ButtonsCentralizationTests`, and
  `ContrastTests` pass; `./visual-relay check` is green including the screenshot render.
- All existing XAML consumers compile and behave unchanged (Click handlers, `Flyout`, `Command`,
  `CommandParameter`, `Appearance`, `Glyph`, `IconStyle`/`ChevronDirection`/`IsContracted`, stage
  card rich content).
- Commit lands on `main` with a Conventional Commit subject per `docs/commit-messages.md`; only the
  button component files, the new ControlThemes `.axaml`, `App.axaml`, and the test were touched.
