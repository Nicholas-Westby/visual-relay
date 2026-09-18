using System.Text.Json;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelInitTests
{
    // ── Startup-inspection isolation ─────────────────────────────────────

    /// <summary>
    /// A host that is runnable on THIS platform, stated rather than resolved.
    /// <para>
    /// With no resolver injected the view model falls back to
    /// <c>SandboxHost.Current</c>, which on Windows is a real wsl.exe probe behind
    /// process-wide memoised state that other tests mutate. When that state said "no
    /// distro", CreateConfig took the refusal and returned without writing anything:
    /// measured on Windows at 0.412, 2 full-suite runs of 2 failed in about 12 ms with
    /// the config missing, while the same test passed in 4 seconds run alone.
    /// </para>
    /// <para>
    /// It is NOT <c>SandboxHost.Local</c>, which is <c>IsWindows: false</c>. Stating
    /// that on Windows is a claim about the platform that is not true: the launch then
    /// takes the POSIX branch and tries to start <c>/bin/sh</c>, which is not there.
    /// Measured the same way, 2 runs of 2. A Windows host WITH a distro is what is
    /// runnable there, and it satisfies the tool gate without touching the filesystem,
    /// because that arm's requirement is a resolved distro rather than a binary on PATH.
    /// </para>
    /// </summary>
    private static Task<SandboxHost> RunnableHost() => Task.FromResult(
        OperatingSystem.IsWindows()
            ? SandboxHost.Windows(new WslContext(
                @"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/bin/nono", "/home/u"))
            : SandboxHost.Local);

    /// <summary>
    /// Answers the validation smoke-run without starting a process. A test that states
    /// a host but lets this default still launches a real shell through that host, so
    /// the platform decides whether it passes; that is what stating the host alone
    /// could not fix.
    /// </summary>
    private static ITestRunner AcceptingRunner(TimeSpan _) =>
        new ScriptedTestRunner(new TestRunResult(0, "green"));

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
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, SandboxHostResolver = RunnableHost };
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
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, SandboxHostResolver = RunnableHost };

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

    /// <summary>
    /// The Windows arm of <see cref="RunnableHost"/>, exercised on any OS by stating the
    /// host rather than waiting to be on Windows. A distro-backed host must satisfy the
    /// tool gate (that arm requires a resolved distro, not a binary on PATH) and must
    /// launch nothing, so CreateConfig gets as far as writing the config. Without this
    /// the Windows branch above is only ever executed on Windows, which is where it was
    /// wrong the last two times.
    /// </summary>
    [Fact]
    public async Task CreateConfig_OnAWindowsHostWithADistro_WritesTheConfig()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = () => Task.FromResult(SandboxHost.Windows(
                new WslContext(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/bin/nono", "/home/u"))),
            InitValidationRunnerFactory = AcceptingRunner,
        };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        Assert.False(viewModel.NeedsInitialization, viewModel.StatusText);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
    }

    [Fact]
    public async Task CreateConfig_WritesConfigAndPopulatesQueue()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, SandboxHostResolver = RunnableHost, InitValidationRunnerFactory = AcceptingRunner };
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
        var viewModel = new MainWindowViewModel { RootPath = repo.Root, SandboxHostResolver = RunnableHost, InitValidationRunnerFactory = AcceptingRunner };
        await viewModel.LoadInitialAsync();
        Assert.True(viewModel.NeedsInitialization);

        viewModel.InitTestCommandInput = "dotnet test";
        await viewModel.CreateConfigCommand.ExecuteAsync(null);

        var raw = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"));
        var root = JsonDocument.Parse(raw).RootElement;
        Assert.True(root.TryGetProperty("authorTests", out _));
    }

}
