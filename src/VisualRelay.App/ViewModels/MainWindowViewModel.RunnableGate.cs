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

        // Greenfield: when the test command is still the placeholder and the project
        // has since gained a recognizable toolchain (a scaffold task ran), adopt the
        // real test command before gating. Best-effort: a no-op for normal repos, and
        // a failure here must never block an otherwise-runnable task.
        try
        {
            await ProjectBootstrapper.TryUpgradePlaceholderTestCommandAsync(RootPath, new GitInvoker());
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
        var missingTools = SandboxedStage.MissingRequiredTools(
            result.Config, EnvironmentAccessor?.GetEnvironmentVariable("PATH"), host: host);
        if (missingTools.Count > 0)
        {
            StatusText = SandboxedStage.MissingToolsMessage(missingTools);
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

    /// <summary>The runner the baseline guard check uses; tests inject a fake.</summary>
    /// <param name="config">The loaded configuration.</param>
    /// <returns>A sandboxed runner unless a factory is installed.</returns>
    private ITestRunner BaselineGuardRunnerFor(RelayConfig config) =>
        BaselineGuardRunnerFactory?.Invoke(config) ?? CreateSandboxedTestRunner(config);
}
