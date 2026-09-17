using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

// The pre-run gate: everything that must hold before a task is allowed to start.
// Split out of MainWindowViewModel.Execution.cs to keep that file under the size guard.
public partial class MainWindowViewModel
{
    // internal (not private) so a VM test can drive the gate directly without
    // launching a run; the App's commands call it the same way.
    internal async Task<bool> EnsureRunnableAsync(string? pendingTaskId)
    {
        // Where the sandbox runs, resolved FIRST and with an await. On Windows that
        // is a six-step wsl.exe probe, and both readers below take its answer
        // synchronously — the placeholder upgrade through its git invoker's WSL
        // routing, the tool-presence gate directly — so resolving it here is what
        // keeps the first gate of a session off the UI thread.
        var host = await ResolveSandboxHostAsync();

        // The sandbox gate comes BEFORE anything that runs a command. The placeholder
        // upgrade below checks candidate test commands, and on a Windows host with no
        // distro that check used to run them on the Windows host with no sandbox —
        // the operator's own project command, executed before the gate that refuses
        // it had a chance to speak. Refusing first is what closes that window.
        if (SandboxRefusal(host, RelayConfigLoader.Defaults(string.Empty)) is { } sandboxRefusal)
        {
            StatusText = sandboxRefusal;
            return false;
        }

        // Greenfield: when the test command is still the placeholder and the project
        // has since gained a recognizable toolchain (a scaffold task ran), adopt the
        // real test command before gating. Best-effort: a no-op for normal repos, and
        // a failure here must never block an otherwise-runnable task.
        try
        {
            await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(
                RootPath, new GitInvoker(),
                InitValidationRunnerFactory?.Invoke(ProjectBootstrapper.CreateConfigValidationTimeout),
                host);
        }
        catch
        {
            // Detection/validation hiccup — fall through to gating on the current config.
        }

        var result = await RelayConfigLoader.TryLoadAsync(RootPath);
        if (!result.IsRunnable)
        {
            _pendingRunTaskId = pendingTaskId;
            NeedsInitialization = result.NeedsInitialization;
            ConfigDiagnostic = result.Status == RelayConfigStatus.Malformed ? result.Diagnostic : null;
            StatusText = result.Status == RelayConfigStatus.Malformed
                ? result.Diagnostic!
                : "No usable .relay/config.json — initialize this project to run.";
            return false;
        }

        if (!IsHuggingFaceConfigured)
        {
            _pendingHfRunTaskId = pendingTaskId;
            StatusText = HfGateMessage;
            return false;
        }

        // Fail fast before launching when the sandbox isn't available on this
        // machine — the user gets an actionable message up front, not a failed
        // stage full of nono advisory noise. Reuse the runner's
        // MissingToolsMessage verbatim so both surfaces never drift. PATH comes from
        // the injected accessor when present (tests), else the real process PATH.
        if (SandboxRefusal(host, result.Config) is { } toolRefusal)
        {
            StatusText = toolRefusal;
            return false;
        }

        // The sealed commit is the last stage: without an identity git refuses it only
        // after every paid stage has run, for every task in the queue.
        if (await CheckGitIdentityAsync(host) is { } identityRefusal)
        {
            StatusText = identityRefusal;
            return false;
        }

        // A guard that already fails on the untouched tree fails for every task in the
        // queue; refusing once here is the difference between one actionable message
        // and one escalation ladder per task. No-op for a repo with no guardCmd.
        var runner = BaselineGuardRunnerFor(result.Config);
        if (await BaselineGuardGate.CheckAsync(RootPath, result.Config, runner) is { } guardRefusal)
        {
            StatusText = guardRefusal;
            return false;
        }

        NeedsInitialization = false;
        ConfigDiagnostic = null;
        return true;
    }

    /// <summary>Where the sandbox runs; tests inject a host so the Windows arm gates anywhere.</summary>
    /// <returns>The resolved sandbox host.</returns>
    private Task<SandboxHost> ResolveSandboxHostAsync() =>
        SandboxHostResolver?.Invoke() ?? SandboxHost.CurrentAsync();

    /// <summary>
    /// Why the sandbox cannot launch here, or null when it can. Asked twice: once
    /// before anything runs a command, and once against the loaded config.
    /// </summary>
    /// <param name="host">The resolved sandbox host.</param>
    /// <param name="config">The configuration whose launch would be gated.</param>
    /// <returns>The refusal message, or null.</returns>
    private string? SandboxRefusal(SandboxHost host, RelayConfig config)
    {
        var missing = SandboxedStage.MissingRequiredTools(
            config, EnvironmentAccessor?.GetEnvironmentVariable("PATH"), host: host);
        if (missing.Count > 0)
            return SandboxedStage.MissingToolsMessage(missing, host);

        // Asked BEFORE any worktree is requested. The distro's git accepts a Windows
        // temp path as one long filename and creates the worktree inside the
        // operator's repository, so a mismatch reported afterwards is reported too
        // late to be worth anything.
        return WorktreeNamespace.Mismatch(RootPath, host);
    }

    /// <summary>
    /// Whether git can name a committer in the workspace, asked where that git runs.
    /// A check that cannot run at all (no git) is not a refusal: the commit stage says so.
    /// </summary>
    /// <param name="host">The resolved sandbox host.</param>
    /// <returns>The refusal, or null.</returns>
    private async Task<string?> CheckGitIdentityAsync(SandboxHost host)
    {
        try
        {
            return GitIdentityCheck is { } check
                ? await check(RootPath, host.Wsl is not null)
                : await GitIdentityGate.CheckAsync(RootPath, new GitInvoker(), host.Wsl is not null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The runner the baseline guard check uses; tests inject a fake.</summary>
    /// <param name="config">The loaded configuration.</param>
    /// <returns>A sandboxed runner unless a factory is installed.</returns>
    private ITestRunner BaselineGuardRunnerFor(RelayConfig config) =>
        BaselineGuardRunnerFactory?.Invoke(config) ?? CreateSandboxedTestRunner(config);
}
