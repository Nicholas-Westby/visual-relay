using System.Text.Json;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class MainWindowViewModelInitTests
{
    // ── Startup-inspection isolation ─────────────────────────────────────

    /// <summary>
    /// This machine, stated rather than resolved. With no resolver injected the view
    /// model falls back to <c>SandboxHost.Current</c>, which on Windows is a real
    /// wsl.exe probe behind process-wide memoised state that other tests mutate. When
    /// that state said "no distro", CreateConfig took the refusal and returned without
    /// writing anything, so these tests failed in about 12 ms with the config missing
    /// while the same test passed in 4 seconds run alone. Measured on Windows at 0.412:
    /// 2 runs of 2 in the full suite, 12 of 12 in isolation.
    /// </summary>
    private static Task<SandboxHost> LocalHost() => Task.FromResult(SandboxHost.Local);

    [Fact]
    public async Task LoadInitialAsync_WithNoRoot_DoesNotTriggerSandboxInspection()
    {
        var viewModel = new MainWindowViewModel(); // default root; no repo on disk
        await viewModel.LoadInitialAsync();

        Assert.False(viewModel.IsSandboxInfoLoading);
        Assert.False(viewModel.IsSandboxInfoAvailable);
    }

    [Fact]
    public async Task StartBackgroundInspections_CompletesSandboxInspection()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.StartBackgroundInspections();

        // LoadSandboxPathsAsync sets IsSandboxInfoLoading=true before its first
        // await and clears it in a finally, so the flag is false once the task
        // completes however it ended — that clearing is the invariant here (a
        // spinner that never stops is the bug). Await the captured task: the
        // inspection spawns one nono subprocess per inherited group when nono
        // is on PATH, so any poll budget would assert the scheduler's mood.
        await viewModel.LastSandboxInspection!;

        Assert.False(viewModel.IsSandboxInfoLoading);
    }

    [Fact]
    public async Task RunSelected_WithNoConfig_BlocksAndFlagsInitialization()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n"); // no WriteConfig
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, SandboxHostResolver = LocalHost };
        await viewModel.LoadInitialAsync();

        Assert.True(viewModel.NeedsInitialization);
        Assert.Equal("alpha", Assert.Single(viewModel.Tasks).Id);

        viewModel.SelectedTask = viewModel.Tasks[0];
        await viewModel.RunSelectedCommand.ExecuteAsync(null);

        Assert.True(viewModel.NeedsInitialization);
        Assert.Contains("initialize", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task NoConfig_PrefillsDetectedTestCommand()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "App.csproj"), "<Project/>");
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, SandboxHostResolver = LocalHost };

        await viewModel.LoadInitialAsync();

        Assert.True(viewModel.NeedsInitialization);
        Assert.Equal("dotnet test", viewModel.InitTestCommandInput);
    }

    /// <summary>
    /// Create config makes the same refusal bootstrap does. On a Windows host with no
    /// usable distro the validation shell has nowhere to run the command the operator
    /// typed except the Windows host itself, unsandboxed, and a command accepted
    /// there could never run in the pipeline.
    /// </summary>
    [Fact]
    public async Task CreateConfig_OnWindowsWithoutADistro_ReportsTheRefusalAndWritesNothing()
    {
        using var repo = TestRepository.Create();
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = () => Task.FromResult(SandboxHost.Windows(null)),
            InitValidationRunnerFactory = _ => new ScriptedTestRunner(new TestRunResult(0, "ok")),
        };
        await viewModel.LoadInitialAsync();

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.Contains("visual-relay: ", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
        Assert.True(viewModel.NeedsInitialization);
    }

    [Fact]
    public async Task CreateConfig_WritesConfigAndPopulatesQueue()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, SandboxHostResolver = LocalHost };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.False(viewModel.NeedsInitialization);
        Assert.Equal("alpha", Assert.Single(viewModel.Tasks).Id);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }

    /// <summary>
    /// The manual "Create config" path must run the same test-layout detection
    /// bootstrap does, so a repo configured this way still gets an
    /// <c>authorTests</c> object for Stage 5 to gate on (rather than silently
    /// having none).
    /// </summary>
    [Fact]
    public async Task CreateConfig_WritesTheAuthorTestsObject()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, SandboxHostResolver = LocalHost };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        var raw = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"));
        var root = JsonDocument.Parse(raw).RootElement;
        Assert.True(root.TryGetProperty("authorTests", out _));
    }

    /// <summary>
    /// The manual action asks the same proposer bootstrap falls back to — an agent run
    /// that reads the project — rather than the old single prompt carrying a list of
    /// the root's entry NAMES, which could not show a nested test project or a script's
    /// text.
    /// </summary>
    [Fact]
    public async Task FindTestCommand_PopulatesInputFromTheProposer()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = LocalHost,
            TestCommandProposerFor = (_, _) => Task.FromResult<string?>("go test ./..."),
        };
        await viewModel.LoadInitialAsync();

        await viewModel.FindTestCommandCommand.ExecuteAsync(null);

        Assert.Equal("go test ./...", viewModel.InitTestCommandInput);
    }

    [Fact]
    public async Task FindTestCommand_WithNoProposerAvailable_SaysSoRatherThanFailing()
    {
        using var repo = TestRepository.Create();
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, IsHuggingFaceConfigured = false, SandboxHostResolver = LocalHost };
        await viewModel.LoadInitialAsync();

        await viewModel.FindTestCommandCommand.ExecuteAsync(null);

        Assert.Contains("enter the command manually", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.InitTestCommandInput);
    }

    [Fact]
    public async Task CreateConfig_UsesCreateConfigValidationTimeout()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        TimeSpan? capturedTimeout = null;
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = LocalHost,
            InitValidationRunnerFactory = timeout =>
            {
                capturedTimeout = timeout;
                return new ScriptedTestRunner(new TestRunResult(0, "green"));
            }
        };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.NotNull(capturedTimeout);
        Assert.Equal(ProjectBootstrapper.CreateConfigValidationTimeout, capturedTimeout!.Value);
        Assert.False(viewModel.NeedsInitialization);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }

    [Fact]
    public async Task CreateConfig_SetsValidatingStatusBeforeValidation()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        string? capturedStatusText = null;
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = LocalHost,
        };
        viewModel.InitValidationRunnerFactory = _ =>
            new StatusCaptureTestRunner(new TestRunResult(0, "green"),
                () => capturedStatusText = viewModel.StatusText);
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.NotNull(capturedStatusText);
        Assert.Contains("Validating", capturedStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.NeedsInitialization);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }

    [Fact]
    public async Task CreateConfig_RejectsTimeoutAndSurfacesReason()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = LocalHost,
            InitValidationRunnerFactory = _ => new TimeoutSimulatingTestRunner()
        };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.Contains("timed out", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.NeedsInitialization);
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }
}
