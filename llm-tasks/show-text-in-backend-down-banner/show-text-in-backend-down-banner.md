# Task: Show text in the backend-down banner

When the model backend is unreachable the main window paints a red banner across
the full window width, directly under the top bar. The banner is supposed to
carry the remediation copy that tells the operator what broke and how to fix it.
Under the state the app actually starts in, it carries nothing: a 1440x900
capture shows a 28 px red bar containing zero glyphs.

The cause is that the banner's visibility and its text come from two different
properties with two different defaults. `IsVisible` is bound to
`!IsBackendReachable`, a `bool` that defaults to `false`, so the banner is
visible from the instant the view-model is constructed. The text is bound to
`BackendStatusMessage`, a `string?` that defaults to `null` and is only ever
assigned by the readiness probe. Between construction and the first probe
result — and permanently in any context that never probes, including the
screenshot harness — the banner is visible and wordless.

The fix is additive: give the banner a non-empty fallback so it can never render
blank. Do **not** fix this by hiding the banner. The "backend down" state is
real in that window (the top bar says so at the same moment), so suppressing the
banner would drop a true warning; and a screenshot-based review stage judges
removals poorly — an added line of pink text on red is an unambiguous visible
diff, whereas a disappeared bar is an absence that is hard to confirm from a
capture.

### Evidence (2026-08-20)

- `src/VisualRelay.App/Views/MainWindow.axaml:27-36` is the banner: a `Border`
  with `Background="#3A1518"`, `BorderBrush="#7A2A30"`,
  `BorderThickness="0,0,0,1"` and `Padding="10,6"`. Its visibility is
  `IsVisible="{Binding !IsBackendReachable}"` (line **31**); its text is a
  separate binding, `Text="{Binding BackendStatusMessage}"` (line **32**), with
  `Foreground="#F2B8BC"` (line **34**). Two properties, two defaults, no
  coupling.
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs:207-215` declares both
  backing fields uninitialized: `private bool _isBackendReachable;` (line
  **212**) and `private string? _backendStatusMessage;` (line **215**). So on a
  freshly constructed view-model `IsBackendReachable == false` and
  `BackendStatusMessage == null` — which is exactly "banner visible, banner
  empty".
- The two are **not** racing. `MainWindowViewModel.cs:255-260`
  (`RefreshBackendStatusAsync`) assigns them on consecutive lines —
  `IsBackendReachable = readiness.IsReady;` then
  `BackendStatusMessage = readiness.Message;` — so once a probe returns they are
  always consistent. The wordless banner is the **pre-probe default state**, not
  an ordering bug.
- A completed probe never leaves the text empty while the banner is visible.
  `src/VisualRelay.Core/Execution/BackendReadinessProbe.cs:49-51` returns
  `new BackendReadiness(true, null)` on success and
  `new BackendReadiness(false, NotReadyMessage())` otherwise; the catch-all at
  line **59** does the same. `NotReadyMessage()` at lines **133-135** returns
  `ErrorHintClassifier.HintFor("connection error")`, which resolves via
  `src/VisualRelay.Domain/ErrorHintClassifier.cs:79-86` to the `ConnectionHint`
  constant at lines **11-14** — a 156-character string beginning
  `Hint: Can't reach the model backend at http://127.0.0.1:4000 …`.
- In the real app the empty state is transient but unbounded up to the probe
  timeout. `src/VisualRelay.App/App.axaml.cs:36` constructs the view-model,
  line **41** binds it as the window's `DataContext`, and line **45** calls
  `StartBackgroundInspections()`, which at
  `src/VisualRelay.App/ViewModels/MainWindowViewModel.Sandbox.cs:54-57` only
  *fires* `_ = RefreshBackendStatusAsync();` — it does not await it. The window
  therefore paints before the probe returns. `BackendReadinessProbe.cs:24` sets
  `DefaultTimeout` to 2 seconds, so a hung (rather than refusing) backend holds
  the wordless banner on screen for up to that long.
- In the screenshot harness the empty state is **permanent**.
  `tools/VisualRelay.Screenshots/Program.cs:90-102` builds
  `new MainWindowViewModel { … }` with object-initializer properties only, and
  the file contains no call to `LoadInitialAsync`, `StartBackgroundInspections`
  or `StartBackendMonitoring`. `RefreshBackendStatusAsync` is private and its
  only callers are `MainWindowViewModel.Sandbox.cs:56`,
  `MainWindowViewModel.cs:272` (the 15 s monitor tick) and
  `MainWindowViewModel.Commands.cs:35,47,75`. None of them runs in the harness,
  so every capture it produces shows the blank banner.
- Confirmed on two real renders, both 1440x900:
  `.relay/colour-run-log-attention-rows/visual-review/main.png` and
  `.relay/show-stage-tier-on-stage-cards/visual-review/main.png` measure
  identically. A vertical slice at x=400 reads `#3A1518` continuously from
  **y=65 to y=91** (27 px), then `#7A2A30` at **y=92** (the 1 px bottom border),
  then the window background `#0C0E12` from y=93. Those are the banner's own
  `Background` and `BorderBrush` literals from `MainWindow.axaml:27-28`.
- The band is genuinely glyph-free. Over the rectangle x=0..1439, y=65..91 there
  is exactly **one distinct colour**, `#3A1518`, across all **38 880** pixels —
  no anti-aliased text pixels of any kind. Row y=92 is `#7A2A30` for all 1440
  px. The band is 27 px of content plus 1 px of border, i.e. sized as though one
  12 px line of text were present (`Padding="10,6"` contributes 12 px of it),
  yet containing none.
- macOS Vision OCR over the same PNG finds `● backend down` at (546,38) — that
  is the top bar's `BackendStatusLabel`, above the banner — and finds no text
  anywhere between y=52 and y=140. The state is real and correctly reported one
  row up; only the banner is mute.
- Only one banner is on screen. The sibling `Border` at `MainWindow.axaml:38-47`
  uses `IsVisible="{Binding !!ControlApiUnavailableBanner}"` (line **42**) —
  visibility derived from its own text — and is fully collapsed in the captures
  (y=93 is already window background), which also confirms that a false
  `IsVisible` reserves no space in this `StackPanel`.
- One existing test pins the current null default and must keep passing:
  `tests/VisualRelay.Tests/MainWindowViewModelInitTests.cs:20` asserts
  `Assert.Null(viewModel.BackendStatusMessage);` after `LoadInitialAsync` with
  no root. The same file polls `viewModel.BackendStatusMessage is not null` at
  lines **58** and **63** as its signal that the fire-and-forget probe
  completed. Seeding a default into `BackendStatusMessage` itself breaks the
  first assertion and makes the second vacuous.
- `tests/VisualRelay.Tests/BackendStatusIndicatorTests.cs` (42 lines) covers
  `BackendStatusBrush`, `BackendStatusLabel` and `StartBackendCommand`
  enablement, all keyed on `IsBackendReachable`. It never references
  `BackendStatusMessage`, so it is unaffected.
- `tests/VisualRelay.Tests/ControlApiTests.cs:51` asserts only that the state
  payload *has* a `message` field (`Assert.True(backend.TryGetProperty("message",
  out _));`), not what it contains. The Control API shape at
  `src/VisualRelay.App/Services/ControlApi.State.cs:32-34` therefore does not
  constrain the fix.

### What to build

Make the backend-down banner always render readable text, by binding its
`TextBlock` to a new computed property that falls back to the probe's standard
not-ready message whenever `BackendStatusMessage` is null or blank.

- Add a read-only computed property — `BackendBannerText` — to
  `src/VisualRelay.App/ViewModels/MainWindowViewModel.Properties.cs`. That
  partial is the established home for computed display properties
  (`BackendStatusBrush` and `BackendStatusLabel` already live there at lines
  **57-60**), and it has the headroom that `MainWindowViewModel.cs` does not.
  Do **not** put it in `MainWindowViewModel.cs`.
- Its logic: return `BackendStatusMessage` when that is a non-empty,
  non-whitespace string; otherwise return the standard not-ready message.
- Keep one source of truth for that copy. `NotReadyMessage()` in
  `src/VisualRelay.Core/Execution/BackendReadinessProbe.cs:133-135` is currently
  `private`; promote it to a public member (e.g. a public
  `NotReadyMessage` property or method on `BackendReadinessProbe`) and have both
  the probe and the fallback use it, so the banner's pre-probe text and its
  post-probe text are literally the same string. Do not paste a second copy of
  the hint text into the view-model. (An acceptable alternative is calling
  `ErrorHintClassifier.HintFor("connection error")` from the shared accessor —
  what matters is that the string exists once.)
- Change `src/VisualRelay.App/Views/MainWindow.axaml:32` to
  `Text="{Binding BackendBannerText}"`. Leave `IsVisible`, `Background`,
  `BorderBrush`, `BorderThickness`, `Padding`, `FontSize`, `Foreground` and
  `TextWrapping` exactly as they are.
- Make the property notify. `_backendStatusMessage` at
  `MainWindowViewModel.cs:214-215` carries no `[NotifyPropertyChangedFor]`
  attributes today; add `[NotifyPropertyChangedFor(nameof(BackendBannerText))]`
  so the banner refreshes when a probe lands. That is a one-line addition to
  `MainWindowViewModel.cs` — see the line-count constraint below.
- Leave `BackendStatusMessage` itself untouched: same type, same `null` default,
  same assignment at `MainWindowViewModel.cs:259`. It is the raw probe result
  and is consumed as such by the top bar tooltip
  (`src/VisualRelay.App/Views/Controls/TopBar.axaml:78`) and the Control API
  (`ControlApi.State.cs:34`). Only the banner's own binding changes.
- Add tests in the style of the existing view-model tests: assert that
  `BackendBannerText` is non-empty on a freshly constructed
  `MainWindowViewModel` (the pre-probe state), that it equals
  `BackendStatusMessage` when that is set to a real message, and that it falls
  back again when the message is set back to `null` or `""`. Put them in
  `tests/VisualRelay.Tests/BackendStatusIndicatorTests.cs`, which is the
  existing home for backend-indicator view-model tests and has ample room.
- A headless render assertion is welcome but optional; if added, follow the
  `[AvaloniaFact]` + `[Collection("Headless")]` pattern used by
  `tests/VisualRelay.Tests/HfGateHintLayoutTests.cs`, and put it in a new file
  rather than growing an existing one.

After the change, a fresh 1440x900 capture from
`tools/VisualRelay.Screenshots` must show the same red band in the same place —
`#3A1518` starting at y=65 with the `#7A2A30` rule below it — but now with a
single line of light-pink `#F2B8BC` text inside it, left-aligned starting about
10 px from the left edge, reading `Hint: Can't reach the model backend at
http://127.0.0.1:4000 — is the LiteLLM proxy running? …`. At 1440 px wide and
12 px font the 156-character message fits on one line, so the band's height
should stay at 27 px of content plus the 1 px border, and nothing below y=93
should move. The visible diff is text appearing inside an otherwise unchanged
bar.

### Out of scope

- Do not hide the banner. Do not change `IsVisible="{Binding
  !IsBackendReachable}"` at `MainWindow.axaml:31` to key off the message, and do
  not add any delay, "first probe completed" flag, or other gate that suppresses
  it. The banner must stay visible in the pre-probe state; only its emptiness is
  the defect.
- Do not change the default of `_isBackendReachable`
  (`MainWindowViewModel.cs:212`) to `true`. The comment at
  `MainWindowViewModel.cs:199-203` and the probe-completion polling at
  `MainWindowViewModelInitTests.cs:53-63` both depend on it starting `false`.
- Do not seed, initialize, or otherwise change `BackendStatusMessage`. That
  breaks `MainWindowViewModelInitTests.cs:20`.
- Do not modify `tools/VisualRelay.Screenshots/Program.cs`. Seeding a message
  into the harness would make the capture look right while leaving the real
  app's startup window still blank.
- Do not touch the sibling Control-API banner at `MainWindow.axaml:38-47`, its
  `!!ControlApiUnavailableBanner` visibility binding, or
  `App.axaml.cs:71-73`.
- Do not change the banner's colours, padding, border, font size, position, or
  the `StackPanel` that hosts it. Do not add an icon, a dismiss control, a
  retry button, or a second line.
- Do not touch the top bar's status dot, label, tooltip, or Start button
  (`TopBar.axaml:74-91`), or `BackendStatusBrush` / `BackendStatusLabel`.
- Do not change the Control API state shape in `ControlApi.State.cs:32-34`.
- Do not change probe timing, the 15 s monitor interval
  (`MainWindowViewModel.cs:269`), the 2 s probe timeout
  (`BackendReadinessProbe.cs:24`), or the retry knobs at
  `BackendReadinessProbe.cs:21-22`.
- Do not reword the existing `ConnectionHint` copy in
  `ErrorHintClassifier.cs:11-14`.

### Constraints

- Hard guard: no `.cs`/`.axaml` under `src/`, `tests/` or `tools/` may exceed
  300 lines (`tools/VisualRelay.Guards/FileSizeGuard.cs:13`, counting via
  `File.ReadAllLines().Length`). Current counts for every file this change
  touches:
  - `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` — **296**. Only
    **4 lines of headroom**. The single `[NotifyPropertyChangedFor]` attribute
    line is all that belongs here; the computed property itself must go in the
    `Properties` partial.
  - `src/VisualRelay.App/ViewModels/MainWindowViewModel.Properties.cs` — **76**.
    224 lines of headroom. This is the destination for `BackendBannerText`.
  - `src/VisualRelay.App/Views/MainWindow.axaml` — **209**. The binding change
    at line 32 is in-place, so the count does not move.
  - `src/VisualRelay.Core/Execution/BackendReadinessProbe.cs` — **136**. Ample
    room to promote `NotReadyMessage()`.
  - `tests/VisualRelay.Tests/BackendStatusIndicatorTests.cs` — **42**. Ample
    room for the new facts.
  If a new partial is needed for any reason, follow the existing naming
  convention (`MainWindowViewModel.<Area>.cs`) rather than growing
  `MainWindowViewModel.cs`.
- `MainWindowViewModel` is a CommunityToolkit.Mvvm `[ObservableProperty]`
  view-model. Computed properties are plain expression-bodied members in the
  partials; change notification comes from `[NotifyPropertyChangedFor]` on the
  source field, not from hand-written `OnPropertyChanged` calls.
- Every existing test must still pass unchanged. In particular
  `MainWindowViewModelInitTests.cs:20`, `:58`, `:63`,
  `BackendStatusIndicatorTests.cs` in full, and `ControlApiTests.cs:51`.
- Avalonia binds `!IsBackendReachable` and `!!ControlApiUnavailableBanner` with
  its built-in negation syntax; keep that idiom if any binding is re-expressed.
