using VisualRelay.Cli;

namespace VisualRelay.Tests;

/// <summary>
/// Validates that every known command, guard subcommand, and backend command
/// is properly dispatched by its respective Program.cs switch expression.
/// Parses source text to assert case-arm coverage — no runtime integration.
/// </summary>
public sealed partial class RunAllModesTests
{
    // ── Run All mode dispatch ─────────────────────────────────────────────

    [Fact]
    public void DrainQueue_PassesRunAllModeToController()
    {
        var paths = RepoPaths.Resolve();
        var executionSource = File.ReadAllText(
            Path.Combine(paths.Root, "src", "VisualRelay.App", "ViewModels",
                "MainWindowViewModel.Execution.cs"));

        // The DrainQueueAsync method must pass SelectedRunAllMode to the
        // controller's DrainAsync call so the mode selection takes effect.
        Assert.Contains("SelectedRunAllMode", executionSource, StringComparison.Ordinal);
        Assert.Contains("DrainAsync(mode:", executionSource, StringComparison.Ordinal);
    }

    // ── CLI Program.cs dispatch coverage ─────────────────────────────────

    [Fact]
    public void CliProgramDispatch_CoversAllKnownCommands()
    {
        var paths = RepoPaths.Resolve();
        var cliProgram = File.ReadAllText(
            Path.Combine(paths.Root, "tools", "VisualRelay.Cli", "Program.cs"));

        // Every known subcommand must have a case arm in the switch expression.
        var knownCommands = new[]
        {
            "launch", "run", "build", "test", "format", "screenshot",
            "run-task", "init", "check", "inspect", "gen-backend-config",
            "guards", "install-hooks", "bump-version", "provision-mxc",
        };

        foreach (var cmd in knownCommands)
        {
            Assert.True(CommandRouter.IsKnown(cmd),
                $"'{cmd}' must be a known command for dispatch coverage");

            // Each command must appear as a string literal in the switch.
            Assert.Contains($"\"{cmd}\"", cliProgram, StringComparison.Ordinal);
        }

        // The switch must have a default/underscore fallback.
        Assert.Contains("_ =>", cliProgram, StringComparison.Ordinal);
    }

    // ── Guards Program.cs dispatch coverage ──────────────────────────────

    [Fact]
    public void GuardProgramDispatch_CoversAllSubcommands()
    {
        var paths = RepoPaths.Resolve();
        var guardsProgram = File.ReadAllText(
            Path.Combine(paths.Root, "tools", "VisualRelay.Guards", "Program.cs"));

        var guardSubcommands = new[]
        {
            "shell-size", "file-size", "source-enumeration", "sync-over-async",
        };

        foreach (var sub in guardSubcommands)
            Assert.Contains($"\"{sub}\"", guardsProgram, StringComparison.Ordinal);

        // Must have a default/underscore fallback.
        Assert.Contains("_ =>", guardsProgram, StringComparison.Ordinal);
    }

    // ── Backend Program.cs dispatch coverage ─────────────────────────────

    [Fact]
    public void BackendProgramDispatch_CoversAllCommands()
    {
        var paths = RepoPaths.Resolve();
        var backendProgram = File.ReadAllText(
            Path.Combine(paths.Root, "tools", "VisualRelay.Backend", "Program.cs"));

        var backendCommands = new[] { "start", "stop", "status" };

        foreach (var cmd in backendCommands)
            Assert.Contains($"\"{cmd}\"", backendProgram, StringComparison.Ordinal);

        // Must have a default usage fallback.
        Assert.Contains("default:", backendProgram, StringComparison.Ordinal);
    }

    // ── Passthrough command method coverage ──────────────────────────────

    [Fact]
    public void PassthroughCommand_DispatchesRunTask_GenBackendConfig_Guards()
    {
        var paths = RepoPaths.Resolve();
        var passthroughSource = File.ReadAllText(
            Path.Combine(paths.Root, "tools", "VisualRelay.Cli", "Commands",
                "PassthroughCommand.cs"));

        // Each passthrough must have a method that forwards to the tool project.
        Assert.Contains("RunTask", passthroughSource, StringComparison.Ordinal);
        Assert.Contains("GenBackendConfig", passthroughSource, StringComparison.Ordinal);
        Assert.Contains("Guards", passthroughSource, StringComparison.Ordinal);

        // The forwarding helper must reference each tool project name.
        Assert.Contains("VisualRelay.RunTask", passthroughSource, StringComparison.Ordinal);
        Assert.Contains("VisualRelay.GenBackendConfig", passthroughSource, StringComparison.Ordinal);
        Assert.Contains("VisualRelay.Guards", passthroughSource, StringComparison.Ordinal);
    }

    // ── CLI switch expression has launch/run alias ───────────────────────

    [Fact]
    public void CliProgramDispatch_LaunchAndRun_ShareSameArm()
    {
        var paths = RepoPaths.Resolve();
        var cliProgram = File.ReadAllText(
            Path.Combine(paths.Root, "tools", "VisualRelay.Cli", "Program.cs"));

        // "launch" and "run" must share the same switch arm (or pattern).
        Assert.Contains("\"launch\" or \"run\"", cliProgram, StringComparison.Ordinal);
    }
}
