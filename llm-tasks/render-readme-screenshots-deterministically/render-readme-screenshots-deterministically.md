# Render the README screenshots deterministically, and refresh them on purpose

`./visual-relay check` ends by rendering the two README screenshots straight
into `docs/images/`, and every run leaves both PNGs modified: the header
carries the assembly version, which bumps with every commit, and the demo run
log stamps its six rows with the wall clock. So the committed images are what
somebody last forgot to revert (rendered at `v0.176`; VERSION is `0.281`), a
`check` on a clean tree ends dirty, and the demo vocabulary has drifted from
the product: a card flagged `swival exit 2` names the subprocess agent retired
on 2026-09-01, and the run-log rows say `model: cheap` where a real
`stage_done` names the model that served the stage. This task makes the render
a pure function of the commit, keeps `check` out of `docs/images/`, turns the
gate's render step into a determinism check, and lands one deliberate refresh.

## Evidence

- Review of 2026-09-11, item 13. Memory note `check-gate-green-at-baseline`
  (2026-09-01): "`check` regenerates `docs/images/*.png` via the screenshot
  step, so it leaves those two files modified in the working tree. Revert
  them". `git log -- docs/images` last touched the PNGs at 297bc835
  (2026-09-01, VERSION `0.177`), whose render reads `v0.176`; the header of
  both committed images shows that label; VERSION is `0.281` at d5bf93cc.
- `tools/VisualRelay.Screenshots/Program.cs:175` `var now = DateTimeOffset.UtcNow;`
  feeds the six `RelayEvent` rows (`:185-196`), and `Domain/RelayEvent.cs:33`
  renders each as `Timestamp.ToLocalTime().ToString("HH:mm:ss")`: the clock
  text changes every second and with the machine's zone. `:140-141` marks the
  Diagnose stage running 42 s before now; that label is relative and stable.
- `ViewModels/MainWindowViewModel.Properties.cs:36-43` `Version` reads
  `VersionHelper.ReadInformationalVersion()` (`Domain/VersionHelper.cs:88-93`,
  the assembly attribute the build stamps from VERSION) and strips the `+sha`.
- `Program.cs:109` seeds `extract-theme-tokens` with the review reason
  `the stage stalled` and `:104-108` four more cards; the committed image's
  fifth card reads `Needs review  swival exit 2`, so the images predate the
  current seed as well as the current version. `:185-196` put `cheap` and
  `balanced` under the `model` key; `Execution/RelayDriver.Events.cs:57` puts
  `cost.Model` there, the priced model alias (`RelayCostEstimator.cs:27-28`),
  which on the default tiers today is `deepseek-flash`.

## Current state (researched at d5bf93cc)

- `tools/VisualRelay.Cli/Commands/CheckCommand.cs:62-78` `BuildAndRenderScreenshots`:
  builds the tool, renders `paths.DocsImage("visual-relay-main.png")` at the
  default 1440x900 and `visual-relay-compact.png` at 1060x720; its exit code
  is the whole check. `ScreenshotCommand.cs:9-21` (`./visual-relay screenshot`)
  renders the same two files to the same place.
- The tool (`Program.cs:10-74`) pins `XDG_CONFIG_HOME` under
  `.relay/scratch/screenshot-root` before building the view model (`:17-25`,
  the precedent for pinning process state), boots a headless `MainWindow`
  with `.UseSkia()` and `.WithInterFont()` (`:209-219`, so glyphs do not
  depend on the machine's fonts), waits 100 ms (`:61`), seeds the view model
  and saves through `PngBitmapEncoderOptions` (`:203-207`). `.relay/*` is
  gitignored except `config.json` (`.gitignore:14-15`).
- Stage 8 uses the same tool through `visualRenderCmd`
  (`.relay/config.json`: `dotnet run --project tools/VisualRelay.Screenshots -- {outDir}/main.png`;
  `Execution/RelayDriver.ReviewPairTriage.cs:52-62` renders into
  `.relay/<task>/visual-review/`). It never writes `docs/images/`.
- README.md:12 embeds `visual-relay-main.png`; nothing embeds the compact one
  (`grep -rn "visual-relay-compact" README.md docs/*.md` is empty).
- No test covers the tool; `SplitGuardVerificationTests.NoTestFile_BootsWholeAppOutsideAllowlist`
  keeps whole-app boots out of ordinary tests, which is why the gate, not a
  test, is where a render is checked.

## Prescribed approach

1. A pure render. `Program.cs` pins the clock and the zone beside the config
   home: `Environment.SetEnvironmentVariable("TZ", "UTC")` before the first
   `ToLocalTime` (the .NET runtime reads `TZ` once, on Unix), and a constant
   `Now = new DateTimeOffset(2026, 9, 11, 10, 46, 0, TimeSpan.Zero)` replaces
   `DateTimeOffset.UtcNow` at `:140-141` and `:175`, so the row clocks and the
   `Running 42s` label are fixed text. The version label is left real: it is
   part of what a refresh records, and it is constant within one commit.
2. `check` renders into scratch and proves determinism. `BuildAndRenderScreenshots`
   renders `main` twice and `compact` once into
   `.relay/scratch/check-screenshots/`, compares the two `main` renders byte
   for byte, and fails with exit 1 and
   `visual-relay: the screenshot render is not deterministic (<n> bytes differ);
   something in the UI reads the clock or the machine` when they differ. It
   never writes under `docs/images/`. `ScreenshotCommand` keeps writing
   `docs/images/`; that verb is the deliberate refresh.
3. Demo vocabulary that matches the product. The fifth card's reason becomes
   `verify red after 3 attempts`; the run-log rows keep the tier in the
   `Tier` field and put `deepseek-flash` under `model`, which is what
   `stage_done` carries today; `SelectedTaskContext` and the trace entries
   stay. Nothing else about the composition changes, so the refreshed image
   still shows every card state the comments at `:119-124` enumerate.
4. One refresh commit, `docs: refresh readme screenshots from the current ui`,
   made by running `./visual-relay screenshot` after the tool change and
   committing both PNGs; the body records the version label they show and the
   byte sizes. Whoever changes the UI afterwards refreshes the same way, and
   `check` no longer nags them to.
5. Docs: AGENTS.md's `check` line (`:21-22`) and README.md's (`:108,126`)
   read "screenshot determinism check" instead of "screenshot render", with
   one sentence in AGENTS.md's dev-only tools list (`:168-169`): `screenshot`
   is how `docs/images/` is refreshed, deliberately, and committed.

## Tests

- `SplitGuardVerificationTests.Conventions.ScreenshotTool_ReadsNoWallClock`:
  `tools/VisualRelay.Screenshots/Program.cs` contains neither `UtcNow` nor
  `DateTimeOffset.Now` nor `DateTime.Now` (a source guard, the family's
  existing shape).
- `CheckCommandScreenshotStepTests` (the CLI's test project, or
  `tests/VisualRelay.Tests` if the CLI has none; see how `WslGateDecisionTests`
  reaches `tools/VisualRelay.Cli`): the step's output directory is under
  `.relay/scratch`, not `docs/images`; two identical renders pass; a
  one-byte difference fails with the message above. Drive it through an
  injected render delegate so the test boots no window.
- The determinism check itself is the runtime test of steps 1 and 3; it runs
  on every `check`.

## Verification

    ./visual-relay check && git status --short docs/images
    ./visual-relay screenshot && git status --short docs/images
    ./visual-relay screenshot && git diff --stat docs/images

Expected: after `check` the status is empty (nothing under `docs/images/`
touched, exit 0); after the first `screenshot` both PNGs are modified; after
the second the diff against the first render is empty (byte-identical on the
same machine and commit). Open the refreshed `visual-relay-main.png` and
confirm the header version, `10:46:00`-series clocks, `deepseek-flash` in the
rows and the new review reason. Then run one task of this repository through
the control API with `visualRenderCmd` in place and confirm
`.relay/<task>/visual-review/main.png` is still produced (stage 8 unchanged).
Put the two PNG sizes and the `check` wall time in the commit body.

## Out of scope

Pixel comparison against the committed images (a UI change would then fail
`check` instead of asking for a refresh); rendering more states or a third
size; the compact image's absence from the README; stage 8's own render
directory.

## Rejected alternatives

- Pinning the version label to a constant: the header would show a version
  that never existed, and the label is stable within a commit anyway, which
  is all the determinism check needs.
- Keeping `check` writing `docs/images/` and adding the PNGs to a
  `git update-index --assume-unchanged` recipe: hides the diff on one clone
  and leaves the refresh accidental.
- Committing whatever `check` renders on every commit: the PNGs would churn
  with VERSION on every commit, 200 KB twice, for no change anyone made.
- Dropping the render from `check` entirely: the step is the only proof that
  the headless app still boots and paints; it stays, pointed at scratch.
