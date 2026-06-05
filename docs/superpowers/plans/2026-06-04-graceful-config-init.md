# Graceful Config & Guided Initialization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Visual Relay usable on any project — and on this repo from a clean clone — without hand-writing `.relay/config.json`: listing never fails on config state, and a no-config folder leads to a one-click guided initialization instead of a dead-end error.

**Architecture:** Replace the single throwing config load with a non-throwing `TryLoadAsync` that returns a `(config, status, diagnostic)` result. Task listing tolerates any status; running gates on it. A shared init capability (detect → write, with optional frontier-LLM discovery) is surfaced by a GUI empty-state panel and a `./visual-relay init` CLI. This repo ships its own committed config.

**Tech Stack:** C# / .NET 10, Avalonia (CommunityToolkit.Mvvm `[ObservableProperty]`/`[RelayCommand]`), xUnit, `System.Text.Json`, local OpenAI-compatible LiteLLM proxy at `ModelBackend.BaseUrl`.

**Conventions:** The repo's `commit-msg` hook enforces Conventional Commits. Every commit subject below is conventional; end each commit body with the trailer `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. New C#/XAML files must stay under 300 lines (`tools/guards/check-file-size.sh`). Run the suite with `./visual-relay test`.

---

## File Structure

**Create:**
- `src/VisualRelay.Domain/RelayConfigResult.cs` — `RelayConfigStatus` enum + `RelayConfigResult` record (config + status + diagnostic; computed `IsRunnable`/`NeedsInitialization`).
- `src/VisualRelay.Core/Init/TestCommandDetector.cs` — marker-based `testCmd` inference.
- `src/VisualRelay.Core/Init/RelayConfigWriter.cs` — writes a minimal valid `.relay/config.json`.
- `src/VisualRelay.Core/Init/LlmTestCommandFinder.cs` — frontier one-shot test-command discovery via the proxy.
- `tools/VisualRelay.Init/Program.cs` + `tools/VisualRelay.Init/VisualRelay.Init.csproj` — headless `init` CLI.
- `.relay/config.json` — this repo's committed config.
- Test files: `RelayConfigResultTests.cs`, `TestCommandDetectorTests.cs`, `RelayConfigWriterTests.cs`, `LlmTestCommandFinderTests.cs` (in `tests/VisualRelay.Tests/`).

**Modify:**
- `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs` — add `TryLoadAsync`; `LoadAsync` becomes a throwing wrapper; optional fields.
- `src/VisualRelay.Core/Tasks/RelayTaskRepository.cs:31,50` — list paths use `TryLoadAsync`.
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` — new observable properties + `_pendingRunTaskId`.
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs` — `ReloadTaskListAsync` sets status props.
- `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs` — `EnsureRunnableAsync` guard + `CreateConfig`/`FindTestCommand` commands.
- `src/VisualRelay.App/Views/Controls/QueuePanel.axaml` — empty-state init panel + malformed banner.
- `.gitignore` — ignore `.relay/*` except `config.json`.
- `visual-relay` — add `init` subcommand.
- `VisualRelay.slnx` — register the Init tool project.
- Existing tests in `RelayConfigLoaderTests.cs` / `RelayTaskRepositoryTests.cs` (add cases).

---

# Phase 1 — Forgiving core

Removes the bug class for both audiences. After this phase, selecting any folder lists its `llm-tasks/` regardless of config state, and Run never proceeds with a bogus command.

## Task 1: Config result types (Domain)

**Files:**
- Create: `src/VisualRelay.Domain/RelayConfigResult.cs`
- Test: `tests/VisualRelay.Tests/RelayConfigResultTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayConfigResultTests
{
    private static RelayConfig AnyConfig() =>
        new("llm-tasks", "dotnet test", "bun test {files}", [],
            new Dictionary<string, string>(), 5, 3, 200, true, true, 1_200_000);

    [Theory]
    [InlineData(RelayConfigStatus.Loaded, true, false)]
    [InlineData(RelayConfigStatus.Defaulted, false, true)]
    [InlineData(RelayConfigStatus.Incomplete, false, true)]
    [InlineData(RelayConfigStatus.Malformed, false, false)]
    public void Flags_FollowStatus(RelayConfigStatus status, bool runnable, bool needsInit)
    {
        var result = new RelayConfigResult(AnyConfig(), status, status == RelayConfigStatus.Malformed ? "bad" : null);
        Assert.Equal(runnable, result.IsRunnable);
        Assert.Equal(needsInit, result.NeedsInitialization);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter RelayConfigResultTests -m:1 -p:UseSharedCompilation=false`
Expected: FAIL — `RelayConfigStatus`/`RelayConfigResult` do not exist (compile error).

- [ ] **Step 3: Create the types**

```csharp
namespace VisualRelay.Domain;

// Classifies how a project's .relay/config.json resolved. Loaded = valid;
// Defaulted = no file (Config is Defaults()); Incomplete = file present but
// required testCmd missing/blank; Malformed = invalid JSON or wrong field type.
public enum RelayConfigStatus
{
    Loaded,
    Defaulted,
    Incomplete,
    Malformed
}

// Result of a non-throwing config load. Diagnostic carries the full, untruncated
// message for the Malformed case (null otherwise).
public sealed record RelayConfigResult(
    RelayConfig Config,
    RelayConfigStatus Status,
    string? Diagnostic)
{
    public bool IsRunnable => Status == RelayConfigStatus.Loaded;

    public bool NeedsInitialization =>
        Status is RelayConfigStatus.Defaulted or RelayConfigStatus.Incomplete;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter RelayConfigResultTests -m:1 -p:UseSharedCompilation=false`
Expected: PASS (4 cases).

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.Domain/RelayConfigResult.cs tests/VisualRelay.Tests/RelayConfigResultTests.cs
git commit -m "feat(config): add RelayConfigResult status type"
```

---

## Task 2: Forgiving loader (`TryLoadAsync`) + optional fields

**Files:**
- Modify: `src/VisualRelay.Core/Configuration/RelayConfigLoader.cs`
- Test: `tests/VisualRelay.Tests/RelayConfigLoaderTests.cs`

- [ ] **Step 1: Write the failing tests** (append to `RelayConfigLoaderTests.cs`, add `using VisualRelay.Domain;` at top)

```csharp
    [Fact]
    public async Task TryLoadAsync_NoFile_ReturnsDefaulted()
    {
        using var repo = TestRepository.Create();
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Defaulted, result.Status);
        Assert.Equal("llm-tasks", result.Config.TasksDir);
    }

    [Fact]
    public async Task TryLoadAsync_OmittedLogSources_DefaultsToEmptyAndLoads()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test" }""");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Empty(result.Config.LogSources);
    }

    [Fact]
    public async Task TryLoadAsync_MissingTestCmd_ReturnsIncomplete()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"), """{ "logSources": [] }""");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task TryLoadAsync_LogSourcesWrongType_ReturnsMalformedWithDiagnostic()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": "logs/app.log" }""");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Malformed, result.Status);
        Assert.Contains("logSources must be an array", result.Diagnostic);
    }

    [Fact]
    public async Task TryLoadAsync_InvalidJson_ReturnsMalformed()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"), "{ not json");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Malformed, result.Status);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter RelayConfigLoaderTests -m:1 -p:UseSharedCompilation=false`
Expected: FAIL — `TryLoadAsync` does not exist (compile error).

- [ ] **Step 3: Implement `TryLoadAsync`, rewrite `LoadAsync` as a wrapper, make fields optional**

In `RelayConfigLoader.cs`, add `using VisualRelay.Domain;` if absent. Replace the body of `LoadAsync` and add `TryLoadAsync` + helpers. Keep `Defaults`, `OptionalString`, `OptionalInt`, `OptionalBool` as-is. Remove `RequiredString` and `ReadStringArray` (replaced below).

```csharp
    public static async Task<RelayConfig> LoadAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var result = await TryLoadAsync(rootPath, cancellationToken);
        return result.Status switch
        {
            RelayConfigStatus.Loaded => result.Config,
            RelayConfigStatus.Defaulted => throw new FileNotFoundException(
                $".relay/config.json not found in {rootPath}",
                Path.Combine(rootPath, ".relay", "config.json")),
            _ => throw new InvalidOperationException(result.Diagnostic ?? "relay config: invalid configuration")
        };
    }

    public static async Task<RelayConfigResult> TryLoadAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var configPath = Path.Combine(rootPath, ".relay", "config.json");
        if (!File.Exists(configPath))
        {
            return new RelayConfigResult(Defaults(), RelayConfigStatus.Defaulted, null);
        }

        JsonDocument doc;
        try
        {
            await using var stream = File.OpenRead(configPath);
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            return new RelayConfigResult(Defaults(), RelayConfigStatus.Malformed,
                $"relay config: invalid JSON in {configPath}: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (!TryGetString(root, "testCmd", out var testCommand) || string.IsNullOrWhiteSpace(testCommand))
            {
                return new RelayConfigResult(Defaults(), RelayConfigStatus.Incomplete,
                    $"relay config: required field testCmd is missing or blank in {configPath}");
            }

            if (!TryReadStringArray(root, "logSources", out var logSources, out var arrayError))
            {
                return new RelayConfigResult(Defaults(), RelayConfigStatus.Malformed, arrayError);
            }

            var defaults = Defaults(testCommand, logSources);
            var tiers = new Dictionary<string, string>(defaults.TierProfiles);
            if (root.TryGetProperty("tierProfiles", out var tierProfiles) && tierProfiles.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in tierProfiles.EnumerateObject())
                {
                    tiers[property.Name] = property.Value.GetString() ?? tiers.GetValueOrDefault(property.Name, property.Name);
                }
            }

            var config = defaults with
            {
                TasksDir = OptionalString(root, "tasksDir", defaults.TasksDir),
                TestFileCommand = OptionalString(root, "testFileCmd", defaults.TestFileCommand),
                TierProfiles = tiers,
                MaxVerifyLoops = OptionalInt(root, "maxVerifyLoops", defaults.MaxVerifyLoops),
                MaxStageFailures = OptionalInt(root, "maxStageFailures", defaults.MaxStageFailures),
                MaxTurns = OptionalInt(root, "maxTurns", defaults.MaxTurns),
                BaselineVerify = OptionalBool(root, "baselineVerify", defaults.BaselineVerify),
                ArchiveOnDone = OptionalBool(root, "archiveOnDone", defaults.ArchiveOnDone),
                SubagentTimeoutMilliseconds = OptionalInt(root, "subagentTimeoutMs", defaults.SubagentTimeoutMilliseconds)
            };
            return new RelayConfigResult(config, RelayConfigStatus.Loaded, null);
        }
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    // Optional array: absent -> empty (ok); present-but-not-array -> error.
    private static bool TryReadStringArray(JsonElement root, string name, out IReadOnlyList<string> values, out string? error)
    {
        error = null;
        if (!root.TryGetProperty(name, out var element))
        {
            values = [];
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            values = [];
            error = $"relay config: {name} must be an array";
            return false;
        }

        values = element.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToArray();
        return true;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter RelayConfigLoaderTests -m:1 -p:UseSharedCompilation=false`
Expected: PASS — the 5 new cases and the existing `LoadAsync_MergesRepositoryConfigWithRelayDefaults`.

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.Core/Configuration/RelayConfigLoader.cs tests/VisualRelay.Tests/RelayConfigLoaderTests.cs
git commit -m "feat(config): non-throwing TryLoadAsync with status and optional fields"
```

---

## Task 3: Task listing tolerates config state

**Files:**
- Modify: `src/VisualRelay.Core/Tasks/RelayTaskRepository.cs` (`ListAsync` ~line 31, `ListCompletedAsync` ~line 50)
- Test: `tests/VisualRelay.Tests/RelayTaskRepositoryTests.cs`

- [ ] **Step 1: Write the failing tests** (append to `RelayTaskRepositoryTests.cs`; add `using VisualRelay.Domain;` if needed)

```csharp
    [Fact]
    public async Task ListAsync_NoConfig_StillListsTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n"); // note: no WriteConfig
        var tasks = await new RelayTaskRepository(repo.Root).ListAsync();
        Assert.Equal(["alpha"], tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task ListAsync_IncompleteConfig_StillListsTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """{ "logSources": [] }""");
        var tasks = await new RelayTaskRepository(repo.Root).ListAsync();
        Assert.Equal(["alpha"], tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task ListAsync_MalformedConfig_ReturnsNoTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), "{ not json");
        var tasks = await new RelayTaskRepository(repo.Root).ListAsync();
        Assert.Empty(tasks);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter RelayTaskRepositoryTests -m:1 -p:UseSharedCompilation=false`
Expected: FAIL — `ListAsync_NoConfig_StillListsTasks` throws `FileNotFoundException` (current `LoadAsync` behavior).

- [ ] **Step 3: Switch both list paths to `TryLoadAsync`**

In `RelayTaskRepository.cs`, add `using VisualRelay.Domain;` if absent. Replace line 31 (in `ListAsync`):

```csharp
        var loaded = await RelayConfigLoader.TryLoadAsync(RootPath, cancellationToken);
        if (loaded.Status == RelayConfigStatus.Malformed)
        {
            return [];
        }

        var config = loaded.Config;
```

Replace line 50 (in `ListCompletedAsync`):

```csharp
        var loaded = await RelayConfigLoader.TryLoadAsync(RootPath, cancellationToken);
        if (loaded.Status == RelayConfigStatus.Malformed)
        {
            return [];
        }

        var config = loaded.Config;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter RelayTaskRepositoryTests -m:1 -p:UseSharedCompilation=false`
Expected: PASS — new cases plus all existing repository tests.

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.Core/Tasks/RelayTaskRepository.cs tests/VisualRelay.Tests/RelayTaskRepositoryTests.cs
git commit -m "fix(tasks): list tasks regardless of config presence"
```

---

## Task 4: Ship this repo's config + gitignore

**Files:**
- Create: `.relay/config.json`
- Modify: `.gitignore`

- [ ] **Step 1: Write the repo config**

Write `.relay/config.json` exactly:

```json
{
  "testCmd": "dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false",
  "testFileCmd": "dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false",
  "logSources": [],
  "tierProfiles": {
    "cheap": "cheap",
    "balanced": "balanced",
    "frontier": "frontier",
    "vision": "vision"
  },
  "baselineVerify": true,
  "archiveOnDone": true,
  "maxTurns": 200
}
```

- [ ] **Step 2: Add gitignore rules**

Append to `.gitignore`:

```
.relay/*
!.relay/config.json
```

- [ ] **Step 3: Verify ignore + tracking**

Run: `git check-ignore .relay/some-task/ledger.md; git check-ignore -v .relay/config.json || echo "config.json NOT ignored (correct)"`
Expected: `.relay/some-task/ledger.md` is ignored; `config.json` prints "NOT ignored (correct)".

- [ ] **Step 4: Confirm it loads as `Loaded`**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter RelayConfigLoaderTests -m:1 -p:UseSharedCompilation=false`
Expected: PASS (sanity — no regression). The committed config matches the `testCmd` and `logSources: []` shape the loader accepts.

- [ ] **Step 5: Commit**

```bash
git add .relay/config.json .gitignore
git commit -m "chore(config): ship repo .relay/config.json and ignore artifacts"
```

---

## Task 5: Run-time guard in the view model

**Files:**
- Modify: `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` (add observable properties + field)
- Modify: `src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs` (`ReloadTaskListAsync`)
- Modify: `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs` (`EnsureRunnableAsync` + wire into run entry points)
- Test: `tests/VisualRelay.Tests/MainWindowViewModelTests.cs`

- [ ] **Step 1: Write the failing test** (append to `MainWindowViewModelTests.cs`)

```csharp
    [Fact]
    public async Task RunSelected_WithNoConfig_BlocksAndFlagsInitialization()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n"); // no WriteConfig
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();

        Assert.True(viewModel.NeedsInitialization);
        Assert.Equal("alpha", Assert.Single(viewModel.Tasks).Id);

        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.RunSelectedCommand.ExecuteAsync(null);

        Assert.True(viewModel.NeedsInitialization);
        Assert.Contains("initialize", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter RunSelected_WithNoConfig -m:1 -p:UseSharedCompilation=false`
Expected: FAIL — `NeedsInitialization` does not exist (compile error).

- [ ] **Step 3a: Add observable properties + field** (in `MainWindowViewModel.cs`, beside the other `[ObservableProperty]` fields around line 90)

```csharp
    [ObservableProperty]
    private bool _needsInitialization;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConfigDiagnostic))]
    private string? _configDiagnostic;

    public bool HasConfigDiagnostic => ConfigDiagnostic is not null;

    // Set when a Run was blocked by a missing config so guided init can resume it.
    private string? _pendingRunTaskId;
```

- [ ] **Step 3b: Set status in `ReloadTaskListAsync`** (in `MainWindowViewModel.Helpers.cs`; add `using VisualRelay.Core.Configuration;` and `using VisualRelay.Domain;` if absent). Replace the method body's opening:

```csharp
    private async Task ReloadTaskListAsync(string? preferredTaskId = null)
    {
        var configResult = await RelayConfigLoader.TryLoadAsync(RootPath);
        NeedsInitialization = !ShowArchive && configResult.NeedsInitialization;
        ConfigDiagnostic = configResult.Status == RelayConfigStatus.Malformed ? configResult.Diagnostic : null;

        var repository = new RelayTaskRepository(RootPath);
        Tasks.Clear();
```

(Leave the rest of the method unchanged.)

- [ ] **Step 3c: Add the guard and wire it in** (in `MainWindowViewModel.Execution.cs`). Add the method:

```csharp
    private async Task<bool> EnsureRunnableAsync(string? pendingTaskId)
    {
        var result = await RelayConfigLoader.TryLoadAsync(RootPath);
        if (result.IsRunnable)
        {
            NeedsInitialization = false;
            return true;
        }

        _pendingRunTaskId = pendingTaskId;
        NeedsInitialization = result.NeedsInitialization;
        ConfigDiagnostic = result.Status == RelayConfigStatus.Malformed ? result.Diagnostic : null;
        StatusText = result.Status == RelayConfigStatus.Malformed
            ? result.Diagnostic!
            : "No usable .relay/config.json — initialize this project to run.";
        return false;
    }
```

In `RunSelectedAsync`, after the `if (SelectedTask is null) return;` guard:

```csharp
        if (!await EnsureRunnableAsync(SelectedTask.Id))
        {
            return;
        }
```

In `DrainQueueAsync`, after the `if (PauseRequested) { ... return; }` block:

```csharp
        if (!await EnsureRunnableAsync(null))
        {
            return;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter "RunSelected_WithNoConfig|MainWindowViewModelTests" -m:1 -p:UseSharedCompilation=false`
Expected: PASS — the new test and all existing VM tests (which `WriteConfig`, so `NeedsInitialization` is false for them).

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.App/ViewModels/MainWindowViewModel.cs src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs tests/VisualRelay.Tests/MainWindowViewModelTests.cs
git commit -m "feat(app): guard runs on config status and expose init state"
```

- [ ] **Step 6: Phase 1 full verification**

Run: `./visual-relay test`
Expected: entire suite PASS.

---

# Phase 2 — Guided initialization

Detect → present → write, shared by a CLI and the GUI empty-state.

## Task 6: Test command detector

**Files:**
- Create: `src/VisualRelay.Core/Init/TestCommandDetector.cs`
- Test: `tests/VisualRelay.Tests/TestCommandDetectorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class TestCommandDetectorTests
{
    [Fact]
    public void Detect_DotnetProject_ReturnsDotnetTest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "App.csproj"), "<Project/>");
        Assert.Equal("dotnet test", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_PythonProject_ReturnsPytest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "pyproject.toml"), "[project]");
        Assert.Equal("pytest", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_NodeProject_ReturnsNpmTest()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "package.json"), "{}");
        Assert.Equal("npm test", TestCommandDetector.Detect(repo.Root));
    }

    [Fact]
    public void Detect_UnknownProject_ReturnsEmpty()
    {
        using var repo = TestRepository.Create();
        Assert.Equal(string.Empty, TestCommandDetector.Detect(repo.Root));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter TestCommandDetectorTests -m:1 -p:UseSharedCompilation=false`
Expected: FAIL — `TestCommandDetector` does not exist.

- [ ] **Step 3: Implement the detector**

```csharp
namespace VisualRelay.Core.Init;

// Best-guess test command for a project root, by build-system markers. Returns
// empty string when the project type is unrecognized (caller falls back to
// manual entry or LLM discovery). .NET is checked first so a .NET repo that also
// has a tests/ directory is not mistaken for Python.
public static class TestCommandDetector
{
    public static string Detect(string rootPath)
    {
        if (HasAnyFile(rootPath, "*.slnx", "*.sln", "*.csproj"))
        {
            return "dotnet test";
        }

        if (File.Exists(Path.Combine(rootPath, "pyproject.toml"))
            || File.Exists(Path.Combine(rootPath, "setup.py"))
            || Directory.Exists(Path.Combine(rootPath, "tests")))
        {
            return "pytest";
        }

        if (File.Exists(Path.Combine(rootPath, "package.json")))
        {
            return "npm test";
        }

        if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
        {
            return "cargo test";
        }

        if (File.Exists(Path.Combine(rootPath, "go.mod")))
        {
            return "go test ./...";
        }

        return string.Empty;
    }

    private static bool HasAnyFile(string rootPath, params string[] patterns) =>
        patterns.Any(pattern =>
            Directory.EnumerateFiles(rootPath, pattern, SearchOption.TopDirectoryOnly).Any());
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter TestCommandDetectorTests -m:1 -p:UseSharedCompilation=false`
Expected: PASS (4 cases).

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.Core/Init/TestCommandDetector.cs tests/VisualRelay.Tests/TestCommandDetectorTests.cs
git commit -m "feat(init): detect test command from project markers"
```

---

## Task 7: Config writer

**Files:**
- Create: `src/VisualRelay.Core/Init/RelayConfigWriter.cs`
- Test: `tests/VisualRelay.Tests/RelayConfigWriterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayConfigWriterTests
{
    [Fact]
    public async Task Write_WithCommand_ProducesLoadableConfig()
    {
        using var repo = TestRepository.Create();
        var path = RelayConfigWriter.Write(repo.Root, "dotnet test");
        Assert.True(File.Exists(path));

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("dotnet test", result.Config.TestCommand);
        Assert.Empty(result.Config.LogSources);
    }

    [Fact]
    public async Task Write_WithEmptyCommand_ProducesIncompleteConfig()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, string.Empty);
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Incomplete, result.Status);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter RelayConfigWriterTests -m:1 -p:UseSharedCompilation=false`
Expected: FAIL — `RelayConfigWriter` does not exist.

- [ ] **Step 3: Implement the writer**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Init;

// Writes a minimal valid .relay/config.json with the given test command and an
// empty logSources array (so the loader treats it as Loaded). Overwrites any
// existing file at that path; callers gate on status before invoking.
public static class RelayConfigWriter
{
    public static string Write(string rootPath, string testCommand)
    {
        var relayDir = Path.Combine(rootPath, ".relay");
        Directory.CreateDirectory(relayDir);

        var json = new JsonObject
        {
            ["testCmd"] = testCommand,
            ["logSources"] = new JsonArray()
        };

        var path = Path.Combine(relayDir, "config.json");
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        return path;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter RelayConfigWriterTests -m:1 -p:UseSharedCompilation=false`
Expected: PASS (2 cases).

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.Core/Init/RelayConfigWriter.cs tests/VisualRelay.Tests/RelayConfigWriterTests.cs
git commit -m "feat(init): write minimal valid relay config"
```

---

## Task 8: `./visual-relay init` CLI

**Files:**
- Create: `tools/VisualRelay.Init/VisualRelay.Init.csproj` (copy of the RunTask csproj)
- Create: `tools/VisualRelay.Init/Program.cs`
- Modify: `VisualRelay.slnx`
- Modify: `visual-relay`

- [ ] **Step 1: Create the project by copying the RunTask csproj**

Run:
```bash
mkdir -p tools/VisualRelay.Init
cp tools/VisualRelay.RunTask/VisualRelay.RunTask.csproj tools/VisualRelay.Init/VisualRelay.Init.csproj
```

- [ ] **Step 2: Write `tools/VisualRelay.Init/Program.cs`**

```csharp
using VisualRelay.Core.Init;

var rootPath = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
if (!Directory.Exists(rootPath))
{
    Console.Error.WriteLine($"visual-relay init: directory not found: {rootPath}");
    return 2;
}

var detected = TestCommandDetector.Detect(rootPath);
var path = RelayConfigWriter.Write(rootPath, detected);

if (string.IsNullOrEmpty(detected))
{
    Console.WriteLine($"Wrote {path} with an empty testCmd — project type was not recognized.");
    Console.WriteLine("Edit testCmd to your project's test command (e.g. \"dotnet test\", \"pytest\", \"npm test\"), then relaunch.");
}
else
{
    Console.WriteLine($"Wrote {path} with testCmd = \"{detected}\".");
}

return 0;
```

- [ ] **Step 3: Register the project in `VisualRelay.slnx`**

In `VisualRelay.slnx`, inside `<Folder Name="/tools/">`, add this line immediately after the `VisualRelay.RunTask` project (4-space indent to match its siblings):

```xml
    <Project Path="tools/VisualRelay.Init/VisualRelay.Init.csproj" />
```

- [ ] **Step 4: Add the `init` subcommand to `visual-relay`**

In `visual-relay`, add `init` to the `needs_dotnet` case list (line ~12):

```bash
  launch|run|build|test|format|screenshot|sample-reset|run-task|init|check)
```

And add a case in the `case "$cmd" in` dispatch (before the `*)` default):

```bash
  init)
    dotnet run --project tools/VisualRelay.Init/VisualRelay.Init.csproj -- "$@"
    ;;
```

Also update the usage line to include `init`.

- [ ] **Step 5: Verify end-to-end against a throwaway dir**

Run:
```bash
rm -rf /tmp/vr-init-demo && mkdir -p /tmp/vr-init-demo && touch /tmp/vr-init-demo/App.csproj
./visual-relay init /tmp/vr-init-demo
cat /tmp/vr-init-demo/.relay/config.json
```
Expected: prints `Wrote .../.relay/config.json with testCmd = "dotnet test".` and the file contains `"testCmd": "dotnet test"`.

- [ ] **Step 6: Commit**

```bash
git add tools/VisualRelay.Init VisualRelay.slnx visual-relay
git commit -m "feat(init): add ./visual-relay init CLI"
```

---

## Task 9: View-model init command + detector prefill

**Files:**
- Modify: `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` (property)
- Modify: `src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs` (prefill in `ReloadTaskListAsync`)
- Modify: `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs` (`CreateConfig` command)
- Test: `tests/VisualRelay.Tests/MainWindowViewModelTests.cs`

- [ ] **Step 1: Write the failing tests** (append to `MainWindowViewModelTests.cs`)

```csharp
    [Fact]
    public async Task NoConfig_PrefillsDetectedTestCommand()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "App.csproj"), "<Project/>");
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };

        await viewModel.LoadInitialAsync();

        Assert.True(viewModel.NeedsInitialization);
        Assert.Equal("dotnet test", viewModel.InitTestCommandInput);
    }

    [Fact]
    public async Task CreateConfig_WritesConfigAndPopulatesQueue()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.False(viewModel.NeedsInitialization);
        Assert.Equal("alpha", Assert.Single(viewModel.Tasks).Id);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter "NoConfig_Prefills|CreateConfig_Writes" -m:1 -p:UseSharedCompilation=false`
Expected: FAIL — `InitTestCommandInput` / `CreateConfigCommand` do not exist.

- [ ] **Step 3a: Add the property** (in `MainWindowViewModel.cs`, beside `_needsInitialization`)

```csharp
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateConfigCommand))]
    private string _initTestCommandInput = string.Empty;
```

- [ ] **Step 3b: Prefill on reload** (in `MainWindowViewModel.Helpers.cs`, inside `ReloadTaskListAsync`, right after setting `NeedsInitialization`). Requires `using VisualRelay.Core.Init;`.

```csharp
        if (NeedsInitialization && string.IsNullOrEmpty(InitTestCommandInput))
        {
            InitTestCommandInput = TestCommandDetector.Detect(RootPath);
        }
```

- [ ] **Step 3c: Add the command** (in `MainWindowViewModel.Execution.cs`; requires `using VisualRelay.Core.Init;`)

```csharp
    private bool CanCreateConfig() => !string.IsNullOrWhiteSpace(InitTestCommandInput);

    [RelayCommand(CanExecute = nameof(CanCreateConfig))]
    private async Task CreateConfigAsync()
    {
        RelayConfigWriter.Write(RootPath, InitTestCommandInput.Trim());
        await RefreshAsync();

        // If a Run was blocked by the missing config, resume it now that it loads.
        if (_pendingRunTaskId is { } pending && !ShowArchive)
        {
            var resumed = Tasks.FirstOrDefault(task => task.Id == pending);
            _pendingRunTaskId = null;
            if (resumed is not null)
            {
                SelectedTask = resumed;
                await RunSelectedCommand.ExecuteAsync(null);
            }
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter "NoConfig_Prefills|CreateConfig_Writes|MainWindowViewModelTests" -m:1 -p:UseSharedCompilation=false`
Expected: PASS — new cases and existing VM tests. (`CreateConfig_Writes` has no pending run, so no Swival is invoked.)

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.App/ViewModels/MainWindowViewModel.cs src/VisualRelay.App/ViewModels/MainWindowViewModel.Helpers.cs src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs tests/VisualRelay.Tests/MainWindowViewModelTests.cs
git commit -m "feat(app): create config from detected command with run resume"
```

---

## Task 10: GUI empty-state panel

**Files:**
- Modify: `src/VisualRelay.App/Views/Controls/QueuePanel.axaml`

- [ ] **Step 1: Hide the task list when initializing**

On the `<ListBox Grid.Row="1" ...>` (line 48), add:

```xml
               IsVisible="{Binding !NeedsInitialization}"
```

- [ ] **Step 2: Add the init panel in the same row** (insert immediately after the closing `</ListBox>` on line 130, still inside the outer `Grid`)

```xml
      <Border Grid.Row="1"
              Margin="16"
              Padding="16"
              VerticalAlignment="Top"
              Background="#1B2129"
              BorderBrush="#2C333E"
              BorderThickness="1"
              CornerRadius="8"
              IsVisible="{Binding NeedsInitialization}">
        <StackPanel Spacing="10">
          <TextBlock Text="Initialize this project"
                     FontWeight="SemiBold"
                     Foreground="#E7ECF3"/>
          <TextBlock Text="No .relay/config.json here yet. Set the command Visual Relay should run to test changes, then create the config."
                     TextWrapping="Wrap"
                     FontSize="12"
                     Foreground="#8E96A3"/>
          <TextBox Text="{Binding InitTestCommandInput}"
                   Watermark="e.g. dotnet test · pytest · npm test · cargo test · go test ./..."
                   FontFamily="Menlo,Consolas,monospace"/>
          <Button Command="{Binding CreateConfigCommand}"
                  Classes="primary"
                  HorizontalAlignment="Left"
                  Padding="12,6"
                  Content="Create config"/>
        </StackPanel>
      </Border>
```

- [ ] **Step 3: Build and verify it renders**

Run: `./visual-relay build`
Expected: build succeeds (XAML compiles).

- [ ] **Step 4: Manual smoke** (the app is a GUI; verify by eye)

Run: `./visual-relay launch`, select a folder with no `.relay/config.json` (e.g. `/tmp/vr-init-demo` after `rm -rf /tmp/vr-init-demo/.relay`).
Expected: the queue area shows the **Initialize this project** panel with the detected command pre-filled; clicking **Create config** replaces it with the task list.

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.App/Views/Controls/QueuePanel.axaml
git commit -m "feat(app): guided init empty-state in the queue panel"
```

- [ ] **Step 6: Phase 2 full verification**

Run: `./visual-relay test`
Expected: entire suite PASS.

---

# Phase 3 — Unknown-project help & error clarity

## Task 11: Frontier LLM test-command finder

**Files:**
- Create: `src/VisualRelay.Core/Init/LlmTestCommandFinder.cs`
- Test: `tests/VisualRelay.Tests/LlmTestCommandFinderTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class LlmTestCommandFinderTests
{
    [Fact]
    public void ExtractCommand_StripsCodeFencesAndQuotes()
    {
        Assert.Equal("pytest", LlmTestCommandFinder.ExtractCommand("```\npytest\n```"));
        Assert.Equal("npm test", LlmTestCommandFinder.ExtractCommand("\"npm test\""));
        Assert.Equal("dotnet test", LlmTestCommandFinder.ExtractCommand("dotnet test\n"));
    }

    [Fact]
    public void BuildPrompt_IncludesTopLevelFileNames()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Cargo.toml"), "[package]");
        var prompt = LlmTestCommandFinder.BuildPrompt(repo.Root);
        Assert.Contains("Cargo.toml", prompt);
    }

    [Fact]
    public async Task FindAsync_ReturnsCleanedCommandFromCompleter()
    {
        using var repo = TestRepository.Create();
        var finder = new LlmTestCommandFinder((_, _) => Task.FromResult("```sh\ncargo test\n```"));
        Assert.Equal("cargo test", await finder.FindAsync(repo.Root));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter LlmTestCommandFinderTests -m:1 -p:UseSharedCompilation=false`
Expected: FAIL — `LlmTestCommandFinder` does not exist.

- [ ] **Step 3: Implement the finder**

```csharp
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using VisualRelay.Domain;

namespace VisualRelay.Core.Init;

// Asks the frontier tier (via the local proxy) for a project's test command.
// The completer seam (prompt -> raw model text) is injectable so prompt assembly
// and response parsing are unit-testable without a network call.
public sealed class LlmTestCommandFinder
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly Func<string, CancellationToken, Task<string>> _complete;

    public LlmTestCommandFinder(Func<string, CancellationToken, Task<string>>? complete = null)
    {
        _complete = complete ?? DefaultCompleteAsync;
    }

    public async Task<string> FindAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        var raw = await _complete(BuildPrompt(rootPath), cancellationToken);
        return ExtractCommand(raw);
    }

    // public (not internal) so the test assembly — which only has InternalsVisibleTo
    // for VisualRelay.App, not Core — can exercise prompt assembly directly.
    public static string BuildPrompt(string rootPath)
    {
        var entries = Directory.EnumerateFileSystemEntries(rootPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Take(100);

        return "You are configuring a CI test command for a project. Given its "
            + "top-level entries, reply with ONLY the shell command that runs its "
            + "test suite — no prose, no code fence.\n\nEntries:\n- "
            + string.Join("\n- ", entries);
    }

    public static string ExtractCommand(string raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            return line.Trim().Trim('"', '`', '\'');
        }

        return string.Empty;
    }

    private static async Task<string> DefaultCompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        var request = new
        {
            model = "frontier",
            messages = new[] { new { role = "user", content = prompt } }
        };

        using var response = await Client.PostAsJsonAsync(
            $"{ModelBackend.BaseUrl}/v1/chat/completions", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter LlmTestCommandFinderTests -m:1 -p:UseSharedCompilation=false`
Expected: PASS (3 cases).

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.Core/Init/LlmTestCommandFinder.cs tests/VisualRelay.Tests/LlmTestCommandFinderTests.cs
git commit -m "feat(init): frontier LLM test-command finder"
```

---

## Task 12: View-model "find it for me" command

**Files:**
- Modify: `src/VisualRelay.App/ViewModels/MainWindowViewModel.cs` (finder seam)
- Modify: `src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs` (`FindTestCommand` command)
- Test: `tests/VisualRelay.Tests/MainWindowViewModelTests.cs`

- [ ] **Step 1: Write the failing test** (append to `MainWindowViewModelTests.cs`; add `using VisualRelay.Core.Init;`)

```csharp
    [Fact]
    public async Task FindTestCommand_PopulatesInputFromFinder()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            TestCommandFinder = new LlmTestCommandFinder((_, _) => Task.FromResult("go test ./..."))
        };
        await viewModel.LoadInitialAsync();

        await viewModel.FindTestCommandCommand.ExecuteAsync(null);

        Assert.Equal("go test ./...", viewModel.InitTestCommandInput);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter FindTestCommand_Populates -m:1 -p:UseSharedCompilation=false`
Expected: FAIL — `TestCommandFinder` / `FindTestCommandCommand` do not exist.

- [ ] **Step 3a: Add an injectable finder** (in `MainWindowViewModel.cs`; add `using VisualRelay.Core.Init;`). Place beside the other fields:

```csharp
    // Injectable so tests can supply a fake completer; defaults to the frontier proxy.
    public LlmTestCommandFinder TestCommandFinder { get; init; } = new();
```

- [ ] **Step 3b: Add the command** (in `MainWindowViewModel.Execution.cs`)

```csharp
    private bool CanFindTestCommand() => IsBackendReachable;

    [RelayCommand(CanExecute = nameof(CanFindTestCommand))]
    private async Task FindTestCommandAsync()
    {
        StatusText = "Asking the frontier model for the test command…";
        try
        {
            var command = await TestCommandFinder.FindAsync(RootPath);
            if (!string.IsNullOrWhiteSpace(command))
            {
                InitTestCommandInput = command.Trim();
                StatusText = "Detected a test command — review it, then Create config.";
            }
            else
            {
                StatusText = "The model didn't return a command — enter one manually.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't reach the model backend: {ex.Message}";
        }
    }
```

- [ ] **Step 3c: Re-evaluate the command when reachability changes** (in `MainWindowViewModel.cs`, on the `_isBackendReachable` field's attributes)

```csharp
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FindTestCommandCommand))]
    private bool _isBackendReachable = true;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj --filter "FindTestCommand_Populates|MainWindowViewModelTests" -m:1 -p:UseSharedCompilation=false`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.App/ViewModels/MainWindowViewModel.cs src/VisualRelay.App/ViewModels/MainWindowViewModel.Execution.cs tests/VisualRelay.Tests/MainWindowViewModelTests.cs
git commit -m "feat(app): find test command via frontier model"
```

---

## Task 13: GUI find button + malformed-config banner

**Files:**
- Modify: `src/VisualRelay.App/Views/Controls/QueuePanel.axaml`

- [ ] **Step 1: Add the "Find it for me" button** to the init panel's `StackPanel`, immediately after the **Create config** `<Button>` (from Task 10). Replace that single button with a horizontal row:

```xml
          <StackPanel Orientation="Horizontal" Spacing="8">
            <Button Command="{Binding CreateConfigCommand}"
                    Classes="primary"
                    Padding="12,6"
                    Content="Create config"/>
            <Button Command="{Binding FindTestCommandCommand}"
                    Padding="12,6"
                    Content="Find it for me"
                    ToolTip.Tip="Ask the frontier model to infer the test command (requires the model backend)"/>
          </StackPanel>
```

- [ ] **Step 2: Add the malformed-config banner** (insert as the first child of the outer `Grid RowDefinitions="Auto,*,Auto"`, spanning row 0 is taken — instead place it at the top of Row 1, before the ListBox, by wrapping. Simplest: add it inside Row 1 with top alignment and its own visibility.)

Insert immediately after the `<ListBox .../>` close and before the init `<Border>` from Task 10:

```xml
      <Border Grid.Row="1"
              Margin="16"
              Padding="14"
              VerticalAlignment="Top"
              Background="#2A1B1E"
              BorderBrush="#7A2A30"
              BorderThickness="1"
              CornerRadius="8"
              IsVisible="{Binding HasConfigDiagnostic}">
        <StackPanel Spacing="6">
          <TextBlock Text="This project's .relay/config.json could not be read"
                     FontWeight="SemiBold"
                     Foreground="#F0B6BC"/>
          <TextBlock Text="{Binding ConfigDiagnostic}"
                     TextWrapping="Wrap"
                     FontFamily="Menlo,Consolas,monospace"
                     FontSize="12"
                     Foreground="#D9A0A6"/>
        </StackPanel>
      </Border>
```

(`HasConfigDiagnostic` is the VM bool added in Task 5; no converter needed. Hiding the ListBox is unnecessary — a malformed config yields an empty list, so the banner sits above an empty area.)

- [ ] **Step 3: Build**

Run: `./visual-relay build`
Expected: build succeeds.

- [ ] **Step 4: Manual smoke**

Run: `./visual-relay launch`; select a folder whose `.relay/config.json` is `{ not json`.
Expected: the red banner shows the full diagnostic (not truncated); no init panel. Then point at a folder with `package.json` only and click **Find it for me** with the backend up → the input fills with a suggested command.

- [ ] **Step 5: Commit**

```bash
git add src/VisualRelay.App/Views/Controls/QueuePanel.axaml
git commit -m "feat(app): LLM find button and malformed-config banner"
```

---

## Final verification

- [ ] **Run the full guard suite**

Run: `./visual-relay check`
Expected: file-size guard passes, format verifies, build succeeds, all tests pass, screenshots render.

- [ ] **Clone smoke (this repo works out of the box)**

Run:
```bash
tmp=$(mktemp -d); git clone . "$tmp/vr" && cd "$tmp/vr" && ls .relay/config.json
```
Expected: `.relay/config.json` is present in the clone (it is tracked), so launching against it lists the real tasks immediately.

---

## Notes for the implementer

- **Run the suite with** `dotnet test tests/VisualRelay.Tests/VisualRelay.Tests.csproj -m:1 -p:UseSharedCompilation=false` (or `./visual-relay test`). The `--filter` examples target individual tests for speed during TDD.
- **Proxy auth:** `LlmTestCommandFinder.DefaultCompleteAsync` posts to the local proxy with no auth header, matching `BackendReadinessProbe`/Swival. If the proxy is configured to require a master key, that wiring is out of scope here — the GUI path degrades to manual entry when the call fails.
- **Don't reformat untouched code** — `./visual-relay check` runs `dotnet format --verify-no-changes`; keep edits minimal so the format gate stays green.
