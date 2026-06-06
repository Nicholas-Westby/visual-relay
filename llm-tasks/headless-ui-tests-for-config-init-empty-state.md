# Drive the full app under Avalonia.Headless: end-to-end UI test for the config-init empty state

The project has rich view-model coverage but **zero tests that exercise the real
control tree**. Every "UI" assertion today reads view-model properties directly
(`MainWindowViewModelInitTests.cs`), so nothing catches a broken XAML binding, a
mis-wired `Command`, or an `IsVisible` toggle that points at the wrong property.
Avalonia already ships a headless backend we depend on for screenshots
(`tools/VisualRelay.Screenshots`), so the machinery to render and drive the
actual `MainWindow` in-process is one package away. This task stands up that
harness in the test project and proves it out on the highest-value flow: the
guided **config-init empty state** in the queue panel.

That flow is the right first target because the logic is *already* unit-tested —
the only untested surface is the wiring between XAML and the view model:

- The init panel and the task list are mutually exclusive via
  `IsVisible="{Binding NeedsInitialization}"` / `IsVisible="{Binding !NeedsInitialization}"`
  (`src/VisualRelay.App/Views/Controls/QueuePanel.axaml`).
- The command box binds `Text="{Binding InitTestCommandInput}"` and the
  **Create config** button binds `Command="{Binding CreateConfigCommand}"`.
- On success `NeedsInitialization` flips false, `.relay/config.json` is written,
  the init `Border` collapses, and the task `ListBox` appears.

A view-model test can't tell you the button is actually clickable, focused, and
bound. A headless test driving real keyboard and mouse input can.

## Current state (researched)

- **Headless is already a proven dependency.** `tools/VisualRelay.Screenshots`
  references `Avalonia.Headless` 12.0.4 and builds the app with a static
  `ScreenshotAppBuilder.BuildAvaloniaApp()` that calls
  `AppBuilder.Configure<App>().UseSkia().UseHeadless(...)`. We mirror that, but
  for the XUnit integration package and without Skia (no pixels needed here).
- **Test project is XUnit 2.9.3** (`tests/VisualRelay.Tests/VisualRelay.Tests.csproj`),
  already references `VisualRelay.App`, and pulls in `Xunit` via a global using.
  This is where the new tests live.
- **`TestRepository`** (`tests/VisualRelay.Tests/TestDoubles.cs`) already creates
  a throwaway repo root under the temp dir with `WriteConfig`/`WriteTask`
  helpers and `IDisposable` cleanup. Reuse it: a repo with **no** `.relay/config.json`
  is exactly what puts `MainWindowViewModel` into `NeedsInitialization`.
- **No `x:Name`s on the init controls.** The init `Border`, the command
  `TextBox`, the **Create config** `Button`, and the task `ListBox` in
  `QueuePanel.axaml` are currently anonymous, so a test can't locate them
  reliably. They need names.

## What to build

### Wire up the headless XUnit harness

- Add `<PackageReference Include="Avalonia.Headless.XUnit" Version="12.0.4" />`
  to `tests/VisualRelay.Tests/VisualRelay.Tests.csproj`. Do **not** add Skia —
  this flow asserts on control state, not rendered pixels, so the default
  headless drawing is fine and keeps the tests fast.
- Add a single harness file, e.g. `tests/VisualRelay.Tests/HeadlessTestApp.cs`,
  with a static app-builder mirroring `ScreenshotAppBuilder` but headless-only:

  ```csharp
  public static class HeadlessTestApp
  {
      public static AppBuilder BuildAvaloniaApp() =>
          AppBuilder.Configure<App>()
              .UseHeadless(new AvaloniaHeadlessPlatformOptions())
              .WithInterFont();
  }
  ```

  and register it once for the assembly:

  ```csharp
  [assembly: AvaloniaTestApplication(typeof(HeadlessTestApp))]
  ```

  Headless UI tests then use `[AvaloniaFact]` (not `[Fact]`) so they run on the
  Avalonia UI thread with a live dispatcher.

### Make the init controls addressable

In `QueuePanel.axaml`, add stable `x:Name`s so the test can locate controls
without walking the tree by index:

- init `Border` → `x:Name="InitEmptyState"`
- command `TextBox` → `x:Name="InitTestCommandBox"`
- **Create config** `Button` → `x:Name="CreateConfigButton"`
- task `ListBox` → `x:Name="TaskQueueList"`

Names only — no behavior, layout, or binding changes.

### Write the end-to-end test

Add `tests/VisualRelay.Tests/ConfigInitEmptyStateUiTests.cs`. The test should
drive the **real window**, not the view model:

1. Create a `TestRepository` with a task but **no config**, point a
   `MainWindowViewModel` at `repo.Root`, and `await LoadInitialAsync()` so it
   settles into `NeedsInitialization == true`.
2. Construct and `Show()` a `MainWindow` with that view model as `DataContext`
   (same pattern as the screenshots tool). Pump the dispatcher
   (`Dispatcher.UIThread.RunJobs()`) so the tree builds.
3. **Assert the empty state renders:** `InitEmptyState.IsVisible` is true and
   `TaskQueueList.IsVisible` is false.
4. **Type via real input:** focus `InitTestCommandBox` and send keyboard text
   using the headless input helpers (`window.KeyTextInput("dotnet test")`),
   then `RunJobs()`. Assert the keystrokes flowed through the binding:
   `viewModel.InitTestCommandInput == "dotnet test"`.
5. **Click the real button:** locate `CreateConfigButton` and trigger a headless
   click (`window.MouseDown`/`window.MouseUp` on the button, or the
   `Avalonia.Headless` click helper), then await the command to settle
   (`RunJobs()` plus the existing `WaitUntilAsync` polling helper).
6. **Assert the outcome end-to-end:** `.relay/config.json` now exists on disk
   and parses; `viewModel.NeedsInitialization` is false; `InitEmptyState.IsVisible`
   is false and `TaskQueueList.IsVisible` is true.

Keep input going through the controls wherever the headless API allows it —
the whole point is to prove the wiring, so prefer `KeyTextInput`/mouse over
poking `viewModel.InitTestCommandInput` directly. If a given step has no stable
headless input affordance, fall back to invoking the bound command and note it
in a comment, but the text-entry and the visibility toggle must go through the
real control tree.

## Done when

- **Harness:** `Avalonia.Headless.XUnit` 12.0.4 is referenced, a single
  `[assembly: AvaloniaTestApplication(typeof(HeadlessTestApp))]` is declared, and
  `[AvaloniaFact]` tests run green on the Avalonia UI thread.
- **Naming:** `QueuePanel.axaml` exposes `InitEmptyState`, `InitTestCommandBox`,
  `CreateConfigButton`, and `TaskQueueList` as `x:Name`s; no other XAML changes.
- **Empty state:** the test shows a real `MainWindow` over a config-less repo and
  asserts the init `Border` is visible and the task `ListBox` is hidden.
- **Real input:** typing into `InitTestCommandBox` via the headless keyboard
  drives `InitTestCommandInput`, and clicking `CreateConfigButton` runs
  `CreateConfigCommand` — both through the control tree, not direct VM calls.
- **Outcome:** after the click, `.relay/config.json` exists and parses,
  `NeedsInitialization` is false, the init `Border` is hidden, and the task
  `ListBox` is visible — all asserted on the live controls.
- **No flake:** the test uses the dispatcher/`WaitUntilAsync` polling already in
  the suite rather than fixed `Task.Delay`s, and cleans up its `TestRepository`.
- This is the project's first headless UI test; the harness file is structured so
  follow-up UI tests (malformed-config banner, archive toggle, stage selection)
  can reuse `HeadlessTestApp` and the `[AvaloniaFact]` pattern.
- `./visual-relay check` green; `dotnet test` green headlessly (no display
  required); C#/XAML files under 300 lines; Conventional Commit subjects.
