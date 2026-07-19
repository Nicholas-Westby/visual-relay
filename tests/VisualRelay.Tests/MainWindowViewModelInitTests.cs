using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class MainWindowViewModelInitTests
{
    // ── Startup-inspection isolation ─────────────────────────────────────

    [Fact]
    public async Task LoadInitialAsync_WithNoRoot_DoesNotTriggerSandboxInspectionOrBackendProbe()
    {
        var viewModel = new MainWindowViewModel(); // default root; no repo on disk
        await viewModel.LoadInitialAsync();

        Assert.False(viewModel.IsSandboxInfoLoading);
        Assert.False(viewModel.IsSandboxInfoAvailable);
        Assert.Null(viewModel.BackendStatusMessage);
    }

    [Fact]
    public async Task StartBackgroundInspections_CompletesSandboxInspection()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.StartBackgroundInspections();

        // LoadSandboxPathsAsync sets IsSandboxInfoLoading=true before its
        // first await, then clears it in a finally. When nono is on PATH the
        // subprocess call yields and the flag stays true; when nono is absent
        // the entire inspection completes synchronously, so the true state
        // may be unobservable by the time we check. Poll for completion
        // instead — the flag transitions back to false in either case.
        for (var i = 0; i < 100; i++)
        {
            if (!viewModel.IsSandboxInfoLoading)
                break;
            await Task.Delay(2); // vr-allow-sleep: polling for fire-and-forget sandbox inspection completion
        }

        Assert.False(viewModel.IsSandboxInfoLoading);
    }

    [Fact]
    public async Task StartBackgroundInspections_TriggersBackendStatusRefresh()
    {
        var viewModel = new MainWindowViewModel();
        viewModel.StartBackgroundInspections();

        // The probe is fire-and-forget (HTTP GET with 2s timeout). With
        // _isBackendReachable defaulting to false, the probe always produces
        // an observable change: IsBackendReachable flips to true when the
        // backend is up, or BackendStatusMessage becomes non-null when it is
        // down. Poll for either signal.
        for (var i = 0; i < 500; i++)
        {
            if (viewModel.IsBackendReachable || viewModel.BackendStatusMessage is not null)
                break;
            await Task.Delay(10); // vr-allow-sleep: fire-and-forget async probe has no exposed completion signal
        }

        Assert.True(viewModel.IsBackendReachable || viewModel.BackendStatusMessage is not null);
    }
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

    [Fact]
    public async Task CreateConfig_UsesCreateConfigValidationTimeout()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        TimeSpan? capturedTimeout = null;
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
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
        };
        viewModel.InitValidationRunnerFactory = timeout =>
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
