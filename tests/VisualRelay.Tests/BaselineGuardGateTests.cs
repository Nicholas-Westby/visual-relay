using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A guard command that already fails on the untouched tree fails for every task, so
/// running the queue can only produce flags. The gate says so before a turn is spent.
/// </summary>
public sealed class BaselineGuardGateTests
{
    private static RelayConfig ConfigWithGuard(string? guardCommand) =>
        RelayConfigLoader.Defaults("dotnet test") with { GuardCommand = guardCommand };

    [Fact]
    public async Task CheckAsync_NoGuardConfigured_Passes()
    {
        var result = await BaselineGuardGate.CheckAsync(
            "/tmp/root", ConfigWithGuard(null), new ScriptedTestRunner(new TestRunResult(1, "boom")));

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckAsync_GuardGreen_Passes()
    {
        var result = await BaselineGuardGate.CheckAsync(
            "/tmp/root", ConfigWithGuard("swift build"), new ScriptedTestRunner(new TestRunResult(0, "")));

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckAsync_GuardRedOnBaseline_RefusesAndQuotesTheCommand()
    {
        var result = await BaselineGuardGate.CheckAsync(
            "/tmp/root", ConfigWithGuard("swift build"),
            new ScriptedTestRunner(new TestRunResult(1, "error: sandbox denied /nix/store")));

        Assert.NotNull(result);
        Assert.Contains("swift build", result, StringComparison.Ordinal);
        Assert.Contains("sandbox denied /nix/store", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureRunnableAsync_GuardRedOnBaseline_SetsStatusAndRefuses()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], guardCmd: "swift build");
        repo.WriteTask("alpha", "# Alpha\n");
        var viewModel = new MainWindowViewModel
        {
            RootPath = repo.Root,
            BaselineGuardRunnerFactory = _ => new ScriptedTestRunner(new TestRunResult(1, "error: no toolchain")),
            IsHuggingFaceConfigured = true,
            // The gates before the guard are stated, not read off this machine: on Windows the tool
            // gate reads the process-wide WSL context, which a parallel test may have replaced.
            SandboxHostResolver = () => Task.FromResult(SandboxHost.Windows(
                new WslContext(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/bin/nono", "/home/u"))),
            GitIdentityCheck = (_, _) => Task.FromResult<string?>(null),
        };

        var runnable = await viewModel.EnsureRunnableAsync(pendingTaskId: null);

        Assert.False(runnable);
        Assert.Contains("swift build", viewModel.StatusText, StringComparison.Ordinal);
        Assert.Contains("no toolchain", viewModel.StatusText, StringComparison.Ordinal);
    }
}
