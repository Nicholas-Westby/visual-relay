using VisualRelay.App.ViewModels;
using VisualRelay.Cli;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// EnsureRunnableAsync tool-presence gate: when the required launch tool (nono,
// the OS sandbox) is not on PATH, the GUI must refuse up front with a clear
// StatusText that names the real cause — and the message must match the runner's
// MissingToolsMessage so both surfaces stay identical.
public sealed partial class MainWindowViewModelTests
{
    [Fact]
    public async Task EnsureRunnableAsync_RequiredToolMissing_SetsClearStatusAndRefuses()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");

        // An injected accessor with an empty PATH drives MissingRequiredTools to
        // see no tools — deterministic, and no process-global env mutation (the
        // convention guard forbids mutating the real process environment in tests).
        var env = new DictionaryEnvironmentAccessor { ["PATH"] = string.Empty };
        // The local host, stated: on a Windows box with WSL this machine's host has nono in the distro.
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root, EnvironmentAccessor = env, SandboxHostResolver = () => Task.FromResult(SandboxHost.Local),
        };
        await viewModel.LoadInitialAsync();

        // Satisfy the HF gate so we reach the tool-presence gate.
        viewModel.IsHuggingFaceConfigured = true;

        var runnable = await viewModel.EnsureRunnableAsync(pendingTaskId: null);

        Assert.False(runnable);
        // Names the real cause: the sandbox binary this host is missing.
        Assert.Contains("nono", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
        // The drifted hand-copy used to omit this sentence; both surfaces must now
        // carry the unified runner message (MissingToolsMessage).
        Assert.Contains("It's set up on the VM, not this host.", viewModel.StatusText, StringComparison.Ordinal);
        // No nono advisory red herrings leak into the gate message.
        Assert.DoesNotContain("deny_shell_configs", viewModel.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("bypass-protection", viewModel.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate probes for the sandbox host ONCE and gates on that answer, so the
    /// Windows arm — where the requirement is a WSL2 distro with nono inside it, not
    /// a binary on the Windows PATH — is what the message names. Injected, because
    /// the real resolution is a wsl.exe probe that only exists on Windows.
    /// </summary>
    [Fact]
    public async Task EnsureRunnableAsync_WindowsHostWithoutADistro_NamesTheWslRequirement()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");

        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            SandboxHostResolver = () => Task.FromResult(SandboxHost.Windows(null)),
        };
        await viewModel.LoadInitialAsync();
        viewModel.IsHuggingFaceConfigured = true;

        var runnable = await viewModel.EnsureRunnableAsync(pendingTaskId: null);

        Assert.False(runnable);
        Assert.Contains("WSL2 distro with nono", viewModel.StatusText, StringComparison.Ordinal);
    }

    /// <summary>
    /// A workspace whose git cannot name a committer would run every stage and fail at
    /// the commit (measured in a fresh WSL distro), so the gate refuses before the
    /// baseline guard spends a build, with the identity fix in the status.
    /// </summary>
    [Fact]
    public async Task EnsureRunnableAsync_GitHasNoIdentity_RefusesBeforeTheGuardRuns()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], guardCmd: "swift build");
        repo.WriteTask("alpha", "# Alpha\n");
        var guardRan = false;
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            IsHuggingFaceConfigured = true,
            GitIdentityCheck = (_, _) => Task.FromResult<string?>("Git has no identity to commit with in this project."),
            BaselineGuardRunnerFactory = _ =>
            {
                guardRan = true;
                return new ScriptedTestRunner(new TestRunResult(0, ""));
            },
        };

        var runnable = await viewModel.EnsureRunnableAsync(pendingTaskId: null);

        Assert.False(runnable);
        Assert.Contains("no identity to commit with", viewModel.StatusText, StringComparison.Ordinal);
        Assert.False(guardRan);
    }

    [Fact]
    public async Task EnsureRunnableAsync_AsksForTheIdentityOfTheWorkspace_InsideTheResolvedDistro()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        (string Root, bool InsideWsl)? asked = null;
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            IsHuggingFaceConfigured = true,
            // A resolved distro satisfies the tool gate on any OS (a local host on Windows has no nono).
            SandboxHostResolver = () => Task.FromResult(SandboxHost.Windows(
                new WslContext(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/bin/nono", "/home/u"))),
            GitIdentityCheck = (root, insideWsl) =>
            {
                asked = (root, insideWsl);
                return Task.FromResult<string?>(null);
            },
        };

        var runnable = await viewModel.EnsureRunnableAsync(pendingTaskId: null);

        Assert.True(runnable, viewModel.StatusText);
        Assert.Equal((repo.Root, true), asked);
    }

    /// <summary>
    /// The host is resolved FIRST, with an await. On Windows that resolution is a
    /// six-step wsl.exe probe, and both readers after it — the placeholder upgrade,
    /// through its git invoker's WSL routing, and the tool-presence gate — take the
    /// answer SYNCHRONOUSLY: whatever runs before the await pays the probe on the
    /// calling thread, which on the first run is the UI thread. Nothing off Windows
    /// can observe the difference, so source order is what holds it.
    /// </summary>
    [Fact]
    public void EnsureRunnableAsync_AwaitsTheSandboxHost_BeforeItsSynchronousReaders()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoPaths.Resolve().Root, "src", "VisualRelay.App", "ViewModels",
            "MainWindowViewModel.RunnableGate.cs"));

        var resolved = source.IndexOf("await ResolveSandboxHostAsync", StringComparison.Ordinal);
        var upgrade = source.IndexOf("TryUpgradePlaceholderTestCommandAsync", StringComparison.Ordinal);
        var gate = source.IndexOf("MissingRequiredTools", StringComparison.Ordinal);

        Assert.True(resolved > 0, "the gate must resolve the sandbox host before it gates");
        Assert.True(resolved < upgrade, "the placeholder upgrade reads the resolved context synchronously");
        Assert.True(resolved < gate, "the tool-presence gate must take the resolved host");
    }
}
