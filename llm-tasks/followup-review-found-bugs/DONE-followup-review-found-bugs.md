# Fix three review-found defects: stale UI copy, loose command type, dead key-setup code

Three small defects were identified during a code review of the post-Settings-fold tree.
Each is a contained, one-file change. They are bundled here because they are all
consequence of the same refactor (provider keys folded into the Settings panel in commit
16cb347) and none depends on the other.

## Current state (researched)

### Bug 1 — Stale user-facing string "open Keys" in `HfGateMessage`

**Production code (`src/VisualRelay.App/ViewModels/MainWindowViewModel.Keys.cs:83`):**

```csharp
: "Set a free Hugging Face token to run tasks — open Keys.";
```

The "Keys" flyout no longer exists; the panel is now "Settings". The correct instruction
is "open Settings." An asserting test pins the old wording:

**Test (`tests/VisualRelay.Tests/KeySetupPanelUiTests.cs:234`):**

```csharp
Assert.Equal("Set a free Hugging Face token to run tasks — open Keys.", vm.HfGateMessage);
```

Both the production string and the assertion must change together.

### Bug 2 — `AttachmentRowViewModel` exposes commands as `ICommand` not `IRelayCommand`

**`src/VisualRelay.App/ViewModels/AttachmentRowViewModel.cs:7,15-16`:**

```csharp
public AttachmentRowViewModel(string path, ICommand revealCommand, ICommand removeCommand)
...
public ICommand RevealCommand { get; }
public ICommand RemoveCommand { get; }
```

The values injected at the only call site
(`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs:203-205`) are
`new RelayCommand(…)` and `new AsyncRelayCommand(…)` — both `IRelayCommand`. Exposing the
wider `System.Windows.Input.ICommand` type means bindings cannot call
`NotifyCanExecuteChanged` through the property. The constructor parameter types and the
two public property types should all be `IRelayCommand`.

### Bug 3 — Dead members `ToggleKeySetup` / `IsKeySetupOpen` in `MainWindowViewModel.Keys.cs`

After the fold, `IsSettingsOpen` and `ToggleSettings` (in
`src/VisualRelay.App/ViewModels/MainWindowViewModel.Settings.cs:44-53`) are what the UI
uses. The old equivalents in `MainWindowViewModel.Keys.cs` have zero external references:

- `_isKeySetupOpen` / `IsKeySetupOpen` (`line 71`) — no XAML binding, no caller outside
  the `ToggleKeySetup` method itself.
- `ToggleKeySetup()` / the generated `ToggleKeySetupCommand` (`lines 93-99`) — no XAML
  `Command="{Binding ToggleKeySetupCommand}"`, no C# `.Execute` call anywhere.

Both members are confirmed unreferenced by a repo-wide grep over `*.cs` and `*.axaml`.

## What to build

TDD: write the corrected assertion / verify the test stays red on the old string, then fix
the production code.

### 1. Fix the stale "open Keys" copy

In `src/VisualRelay.App/ViewModels/MainWindowViewModel.Keys.cs:83`, change the `HfGateMessage`
expression body to:

```
"Set a free Hugging Face token to run tasks — open Settings."
```

In `tests/VisualRelay.Tests/KeySetupPanelUiTests.cs:234`, update the `Assert.Equal` to
assert the same new string.

No other callers reference the literal; the two files are the only change.

### 2. Tighten `AttachmentRowViewModel` command type to `IRelayCommand`

In `src/VisualRelay.App/ViewModels/AttachmentRowViewModel.cs`:

- Replace `using System.Windows.Input;` with `using CommunityToolkit.Mvvm.Input;`.
- Change both constructor parameter types from `ICommand` to `IRelayCommand`.
- Change both public property types from `ICommand` to `IRelayCommand`.

Before changing, verify that the only call site
(`src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs` around line 202-205`)
passes `RelayCommand` / `AsyncRelayCommand` — both implement `IRelayCommand`, so no cast
or wrapper is needed. Confirm no other constructor call site exists.

### 3. Remove dead `ToggleKeySetup` / `IsKeySetupOpen` members

Before deleting, re-run the repo-wide check:

```
grep -rn "ToggleKeySetupCommand\|IsKeySetupOpen\|ToggleKeySetup" src/ tests/ --include="*.cs" --include="*.axaml"
```

Confirm the only hits are the three definition lines inside `MainWindowViewModel.Keys.cs`
(lines 71, 94, 96-97). Then delete:

- The `[ObservableProperty] private bool _isKeySetupOpen;` block (lines 69-71, including
  the XML doc comment on line 69).
- The `[RelayCommand] private void ToggleKeySetup()` block (lines 93-99, including the
  blank line separator).

No test references these members; no deletion of test code is required.

### Bug 4 — `StartBackendAsyncTests` can pass vacuously: no `Flush()`, `Debug.WriteLine` is a no-op on release builds

**Test (`tests/VisualRelay.Tests/StartBackendAsyncTests.cs:9–43`, added in commit `6258317`):**

```csharp
[Fact]
public async Task StartBackendAsync_LogsWhenToolchainIsMissing()
{
    var viewModel = new MainWindowViewModel();
    var writer = new StringWriter();
    var listener = new TextWriterTraceListener(writer);
    Trace.Listeners.Add(listener);
    try
    {
        ...
        await task;
        var output = writer.ToString();
        Assert.NotEmpty(output);   // ← reads from StringWriter without flushing first
    }
    finally
    {
        Trace.Listeners.Remove(listener);
        listener.Dispose();
    }
}
```

Two defects:

1. **No `Flush()` before `writer.ToString()`** (`StartBackendAsyncTests.cs:35`).
   `TextWriterTraceListener` buffers writes and only flushes to the underlying `TextWriter`
   on explicit `Flush()` or `Dispose()`. `writer.ToString()` before either means the
   captured trace output may be empty even when `Debug.WriteLine` was called — the assertion
   `Assert.NotEmpty(output)` passes vacuously (empty string on a listener that has not been
   flushed).

2. **`Debug.WriteLine` is a no-op on non-DEBUG builds** (`Debug` methods are decorated
   `[Conditional("DEBUG")]` in .NET). CI typically runs with `dotnet test -c Release` or
   without the `DEBUG` define, so the production code path under test writes nothing, the
   listener captures nothing, and `Assert.NotEmpty(output)` on an empty string fails — or,
   after the flush fix, asserts on zero bytes and fails for the wrong reason.

**Fix:**

- Add `listener.Flush()` immediately before `var output = writer.ToString()` at
  `StartBackendAsyncTests.cs:35`.
- Replace the `Debug.WriteLine` in production code (`MainWindowViewModel` catch block)
  with `Trace.WriteLine` (which is NOT conditional on `DEBUG` and is always active) or use
  a `ILogger`/`DiagnosticSource` path that fires in release builds.
- Change the assertion from `Assert.NotEmpty(output)` to something that is robust
  regardless of build config: either assert on the method's observable side-effect (e.g.
  the `MainWindowViewModel` state changes, or the backend status reflects the failure) rather
  than debug trace output; OR keep the trace assertion but confirm that the production code
  uses `Trace.WriteLine` (not `Debug.WriteLine`) so the test is meaningful in release builds.

The safest fix that minimises churn: in production, change `Debug.WriteLine` to
`Trace.WriteLine`. In the test, add `listener.Flush()` before the assertion. Both files
change together.

## Done when

- **Bug 1:** `vm.HfGateMessage` returns `"Set a free Hugging Face token to run tasks — open Settings."` when HF_TOKEN is absent. The existing `Assert.Equal` in `KeySetupPanelUiTests.cs:234` passes with the updated expected string. No other test asserts the old wording.
- **Bug 2:** `AttachmentRowViewModel.RevealCommand` and `.RemoveCommand` are typed `IRelayCommand`. The constructor parameters are `IRelayCommand`. The `using System.Windows.Input` import is removed (or replaced). `./visual-relay check` compiles without the `ICommand` reference in that file.
- **Bug 3:** `_isKeySetupOpen` / `IsKeySetupOpen` and `ToggleKeySetup` / `ToggleKeySetupCommand` are absent from `MainWindowViewModel.Keys.cs`. The grep above returns no hits in `src/` or `tests/`.
- **`./visual-relay check` green** after all three changes.
- **No file exceeds 300 lines** added or modified (all three target files are well under that limit before and after).
- **Conventional Commit subject**, e.g.:
  `fix(vm): correct HfGateMessage copy, tighten IRelayCommand type, remove dead ToggleKeySetup`
