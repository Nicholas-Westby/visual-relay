# Centralize Colors and Font Sizes, Then Make Them More Accessible

## Problem / motivation

Colors and font sizes are hard-coded literally throughout the UI, so there is no way to audit — or
improve — the palette or the type scale without reading every view. Today the tree carries **~235 raw
hex literals** across `src/**/*.axaml` and **~107 inline `FontSize="…"`** attributes spread over 15
views, plus a handful of programmatic sites in C#. A color like `#2A303A` recurs in several card borders
with no name telling you it is "the same" border; the font scale is an undocumented spread of
`10, 11, 12, 13, 14, 16`, including sizes small enough to be a legibility problem.

This one task does three inseparable things:

1. Centralize every color and font size into named **design tokens** (Avalonia resources) and make
   **all** code reference those tokens.
2. Add a guard test that **fails if any raw color or font-size literal reappears**.
3. Once everything resolves to a token, **tidy the token values to be more accessible** — fix low
   contrast and raise too-small text to meet WCAG. This is the real payoff: the centralization exists so
   this cleanup is possible in one small, reviewable place.

There is **no ratchet and no baseline of grandfathered violations** — add the guard first, let it
enumerate every offending file, migrate them all, then adjust the token values, and land it with every
gate green.

## The single source of truth

Avalonia already separates `Application.Resources` (a `ResourceDictionary`) from `Application.Styles`
(a `Styles` collection). Tokens are resources; put them there. `App.axaml`
(`src/VisualRelay.App/App.axaml`) currently declares only `<Application.Styles>` (a `FluentTheme` plus
`StyleInclude` of `VisualRelayTheme.axaml`) and has **no** `<Application.Resources>` yet — add it.

Create the tokens in **new** files (the ≤300-line file-size guard forbids stuffing them into the already
large `VisualRelayTheme.axaml`):

- `src/VisualRelay.App/Theme/Colors.axaml` — a `ResourceDictionary` defining a **semantic** palette. For
  each distinct color in use, define a `Color` and a matching `SolidColorBrush` (e.g. `SurfaceBrush`,
  `SurfaceRaisedBrush`, `BorderSubtleBrush`, `TextPrimaryBrush`, `TextMutedBrush`, `AccentBrush`,
  `AccentHoverBrush`, `WarningBrush`, `WarningTextBrush`, `DropIndicatorBrush`, …). **Collapse exact
  duplicates** — the several borders that are all `#2A303A` become one `BorderSubtleBrush`; that
  de-duplication is the audit win.
- `src/VisualRelay.App/Theme/Typography.axaml` — a `ResourceDictionary` of `x:Double` font-size tokens,
  **one per distinct value currently in use** (`10, 11, 12, 13, 14, 16`), named by role (e.g.
  `FontSizeCaption`, `FontSizeBody`, `FontSizeTitle` — you choose the mapping). Do **not** invent new
  sizes at this step; every existing `FontSize` must map onto one of these (values are adjusted later,
  under accessibility).

Merge both into `App.axaml` via `<Application.Resources><ResourceDictionary.MergedDictionaries>` with a
`<ResourceInclude Source="avares://VisualRelay.App/Theme/Colors.axaml"/>` (and Typography). The app uses
a single fixed `RequestedThemeVariant="Dark"` — keep it to **one** palette; do **not** add light/dark
`ThemeDictionaries` the app does not use.

These two token files are the **only** place raw color/size literals may live. Everything else references
them.

## Migrate every usage to tokens (value-preserving)

Replace literals with resource references everywhere, carrying the **exact current values** so this step
changes nothing visually:

- **Style setters** in `src/VisualRelay.App/Styles/VisualRelayTheme.axaml` — e.g. the `Selector="Button"`
  setter `<Setter Property="Background" Value="#1A1E25"/>` becomes
  `Value="{DynamicResource SurfaceRaisedBrush}"`; `Button.primary`'s `#5B7CFA`, the `Button.warning`
  swatch, the `ListBoxItem.drop-above`/`drop-below` `#3191FF`, the `Border.queueCard` colors, etc.
- **Inline attributes** in every view under `src/VisualRelay.App/Views/**` — `Foreground="…"`,
  `Background="…"`, `BorderBrush="…"`, and `FontSize="…"` become `{DynamicResource …}` (brushes) /
  `{StaticResource …}` (font sizes). Use `DynamicResource` for brushes, `StaticResource` for the
  `x:Double` font sizes (which never change at runtime); the guard accepts either markup extension.
- **The `#`-color inside `BoxShadow` and any `GradientStop`** — the color component must reference a
  token; keep offsets/blur numeric.
- **Programmatic sites in C#** (few, but they must not become an escape hatch):
  - `src/VisualRelay.App/Views/Controls/ChevronIcon.cs` — the `Foreground` `StyledProperty` default
    `new SolidColorBrush(Color.Parse("#6F7785"))`, and the `Foreground ?? Brushes.Gray` fallback in its
    render path.
  - `src/VisualRelay.App/Views/Controls/FocusToggleIcon.cs` — the same `Foreground ?? Brushes.Gray`
    fallback.
  - `src/VisualRelay.App/ViewModels/TaskRowViewModel.cs` — `RailBrush`/`RunningBrush`/`SelectedBrush`
    and `Brushes.Transparent`.
  - `src/VisualRelay.App/Views/Controls/Buttons/CommonButton.cs` — `FontSize = 14`.
  - `src/VisualRelay.App/App.axaml.cs` — the confirmation-dialog factory's `FontSize = 13`.

  Prefer moving control-default brushes/sizes **out of C# into theme `Style` setters** that use the
  tokens (a `StyledProperty` default evaluates at type-init, before app resources exist, so it cannot
  take a `DynamicResource`). For genuinely code-built UI, resolve the token from resources
  (`Application.Current!.FindResource("…")`). Note: `ChevronIcon`'s foreground default is asserted
  non-null by an existing test, `ChevronForeground_HasExplicitDefault_NotNull` in
  `tests/VisualRelay.Tests/ChevronAffordanceRenderTests.cs`; if you relocate that default to a Style,
  update that assertion to check the styled/token value rather than deleting or weakening it.

Only two literal color values are exempt from tokenizing (they are not palette colors): `Transparent`
and `{x:Null}`. Everything else resolves to a token.

## The guard: no raw literals may return

Add `tests/VisualRelay.Tests/DesignTokenCentralizationTests.cs`, modeled structurally on the existing
`ButtonsCentralizationTests` (`tests/VisualRelay.Tests/ButtonsCentralizationTests.cs`): discover the
repo via `RepoSetup.Root`, enumerate source files, collect every offender as
`relativePath:line → text`, and assert the violation list is empty with a message that **lists every
offender** so the failure is the worklist. Allow-list the two token files (`Theme/Colors.axaml`,
`Theme/Typography.axaml`) as the sole place literals may appear.

Follow the `FileSizeGuard` seam (`tools/VisualRelay.Guards`, unit-tested by `FileSizeGuardTests`): put
the classification in a **pure** function (e.g. `DesignTokenGuard.FindViolations(files)`) covered by
in-memory unit tests, and a separate real-tree `[Fact]` that runs it over `src/VisualRelay.App` and
asserts zero.

Rules the guard enforces:

1. **No `#`-hex color literal** (`#RGB`/`#RRGGBB`/`#AARRGGBB`) anywhere in `.axaml` outside the two
   token files. A hex literal has one unambiguous lexical form, so a scan is complete for this case and
   catches inline attributes, `Setter` values, `BoxShadow`, and gradient stops alike.
2. **`FontSize` must be a resource reference.** For a `FontSize="…"` attribute or a
   `<Setter Property="FontSize" Value="…"/>`, the value must be `{StaticResource …}` or
   `{DynamicResource …}`; a numeric literal (`FontSize="12"`) fails. Scope this rule to `FontSize` only —
   raw numbers on `Width`, `Padding`, `Opacity`, `CornerRadius`, `BorderThickness`, etc. are out of scope
   (this task is colors and font sizes, nothing else).
3. **Named-color keywords** (`Red`, `White`, `Gray`, …) in the brush-bearing attributes (`Foreground`,
   `Background`, `BorderBrush`, `Fill`, `Stroke`) must be a resource reference, except the two exempt
   literals `Transparent` and `{x:Null}`.
4. **C# scan** over `src/VisualRelay.App/**/*.cs` (model on `ButtonsCentralizationTests.FindNewButtonExpressions`):
   flag `Color.Parse("#…")`, `Color.FromRgb`/`FromArgb`, `Colors.<Name>`, `Brushes.<Name>`,
   `new SolidColorBrush(`, and `FontSize = <numeric-literal>`. Allow-list a single constants file if one
   is introduced for type-init-safe defaults.

Rules 1 and 3 are the allow-list model we want (a styling value must resolve to a token); rule 1 uses a
scan only because hex has a single form, so scanning is already exhaustive for it.

**Add the guard first.** Its first run over the current tree enumerates every file to change — that list
*is* the migration worklist. Then migrate until the guard is green. Because there is no baseline, the
guard must be **red on the pre-migration tree and green after**.

## Tidy the centralized values to be more accessible

With every color and size resolving to one token, fix the accessibility problems the scattered literals
were hiding. The migration above is value-preserving; the adjustments here are the **only** intended
visual changes, and each must be justified against a WCAG 2.2 AA target.

- **Contrast.** Audit each foreground/background token pairing against WCAG 2.2 AA: **4.5:1** for normal
  text, **3:1** for large text (≥ 18px, or ≥ 14px bold) and for non-text UI (structural borders, the
  drop/focus indicators, state-carrying icons). Where a pair fails, nudge the token's lightness to meet
  the threshold while preserving its hue/identity. The likely offenders in the current palette are the
  **muted text**, the **warning** swatch (`#F0CA66` on `#2B2416`), and the **accent/link** colors
  (`#5B7CFA` / `#5575F2`) — verify all of them, not just these.
- **Type size.** Raise the floor of the scale: no text token below **12px**. Remap the former `10` and
  `11` up to at least `12` (the `11` is the most common size, so this is a deliberate, visible legibility
  change — verify no layout or density breaks). Keep the scale coherent; do not add sizes beyond the
  existing tokens.
- **Lock it in.** Add `tests/VisualRelay.Tests/DesignTokenAccessibilityTests.cs`: a **pure** WCAG
  relative-luminance / contrast-ratio function (unit-tested against known pairs — e.g. black-on-white =
  21:1, identical colors = 1:1), plus a `[Fact]` that resolves the token colors (parse
  `Theme/Colors.axaml`) and asserts every declared foreground/background pairing meets its AA threshold,
  and that no font-size token in `Theme/Typography.axaml` is below 12. Encode the real pairings as an
  explicit table in the test — the design's actual text-on-surface combinations — so any future token
  edit that breaks contrast fails here. Same pure-function-plus-real-data shape as
  `FileSizeGuard`/`FileSizeGuardTests`.

## Precedent to model on

- `tests/VisualRelay.Tests/ButtonsCentralizationTests.cs` — same problem family (a UI centralization
  rule enforced by scanning `.axaml` + `.cs`, allow-listing the central home, emitting a numbered
  offender list, asserting zero). Copy its shape.
- `tools/VisualRelay.Guards/FileSizeGuard` + `tests/VisualRelay.Tests/FileSizeGuardTests.cs` — the
  pure-`FindViolations` + real-tree-`Enumerate` split, exercised with in-memory/temp inputs.
- `RepoSetup.Root` — the repo-root discovery helper both guards use.

## Constraints & done criteria

- **The centralization guard is red on the pre-migration tree and green after** (`Failed: 0`). No
  baseline, no suppression list, no grandfathered offenders.
- **The pure classifier is unit-tested** in-memory: a hex line fails; `{DynamicResource SurfaceBrush}`
  passes; `Transparent` and `{x:Null}` pass; `FontSize="12"` fails while `FontSize="{StaticResource
  FontSizeBody}"` passes; a raw number on a non-styling attribute (`Width="12"`) is ignored.
- **Centralization is value-preserving; the accessibility pass is the only intended visual change.**
  After migrating, the app renders identically; then the deliberate accessibility adjustments (contrast
  to AA, 12px type floor) are the only pixels that move, each justified by a WCAG target. Verify the
  running app.
- **Every distinct existing font size (`10, 11, 12, 13, 14, 16`) maps to exactly one token** and no
  `FontSize` literal remains; after the accessibility pass **no font-size token is below 12px**.
- **Every declared foreground/background pairing meets WCAG 2.2 AA** (4.5:1 text / 3:1 large-text & UI),
  enforced by `DesignTokenAccessibilityTests`; its contrast function is unit-tested (black-on-white =
  21:1).
- **Never weaken, skip, or delete a test** to satisfy any gate (VR's own guards forbid `Skip`/deletion);
  update the Chevron default-foreground assertion as described rather than removing it.
- **Keep every new/edited `*.cs`/`*.axaml` file ≤ 300 lines** — hence tokens in separate `Theme/*.axaml`
  files, and split a test file if it grows past the limit.
- Do not tokenize or alter non-color/size literals (margins, padding, radii, thicknesses, durations) —
  out of scope.

## Files likely in scope (the plan stage will finalize the manifest)

- `src/VisualRelay.App/Theme/Colors.axaml` (new) — semantic color + brush tokens (accessibility-adjusted values)
- `src/VisualRelay.App/Theme/Typography.axaml` (new) — font-size tokens (12px floor)
- `src/VisualRelay.App/App.axaml` — add `<Application.Resources>` merging the token dictionaries
- `src/VisualRelay.App/Styles/VisualRelayTheme.axaml` — setters reference tokens
- `src/VisualRelay.App/Views/**/*.axaml` — inline color/`FontSize` literals → tokens (15 views carry
  `FontSize`; most carry hex)
- `src/VisualRelay.App/Views/Controls/ChevronIcon.cs`, `FocusToggleIcon.cs`,
  `Buttons/CommonButton.cs`, `ViewModels/TaskRowViewModel.cs`, `App.axaml.cs` — programmatic sites
- `tests/VisualRelay.Tests/DesignTokenCentralizationTests.cs` (new) — raw-literal guard + pure-classifier unit tests
- `tests/VisualRelay.Tests/DesignTokenAccessibilityTests.cs` (new) — WCAG contrast + min-size assertions
- `tests/VisualRelay.Tests/ChevronAffordanceRenderTests.cs` — update the foreground-default assertion
