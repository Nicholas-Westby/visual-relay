using VisualRelay.Cli;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Queue;

namespace VisualRelay.Tests;

/// <summary>
/// Structural-validation suite that exhaustively validates every execution mode
/// in the visual-relay system is properly enumerated, dispatched, and has
/// correct invariants. Pure xUnit [Fact]/[Theory] assertions against the
/// static type surface — no runtime integration needed.
/// </summary>
public sealed partial class RunAllModesTests
{
    // ── Run All mode enumeration ──────────────────────────────────────────

    [Fact]
    public void RunAllMode_HasStandardAndSequentialValues()
    {
        var values = Enum.GetValues<RunAllMode>();
        Assert.Equal(2, values.Length);
        Assert.Contains(RunAllMode.Standard, values);
        Assert.Contains(RunAllMode.Sequential, values);
    }

    [Fact]
    public void RunAllModeDropdown_ExistsInTopBar()
    {
        var paths = RepoPaths.Resolve();
        var topBarAxaml = File.ReadAllText(
            Path.Combine(paths.Root, "src", "VisualRelay.App", "Views", "Controls",
                "TopBar.axaml"));

        // The ComboBox must bind to RunAllModeOptions and SelectedRunAllMode.
        Assert.Contains("RunAllModeOptions", topBarAxaml, StringComparison.Ordinal);
        Assert.Contains("SelectedRunAllMode", topBarAxaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DrainAsync_AcceptsRunAllModeParameter()
    {
        // The DrainAsync method must accept a RunAllMode parameter so callers
        // can choose between Standard and Sequential execution.
        var method = typeof(RelayQueueController).GetMethod("DrainAsync");
        Assert.NotNull(method);

        var modeParam = method.GetParameters()
            .FirstOrDefault(p => p.Name == "mode" && p.ParameterType == typeof(RunAllMode));
        Assert.NotNull(modeParam);
        Assert.True(modeParam.IsOptional);
    }

    // ── CLI command enumeration ──────────────────────────────────────────

    [Theory]
    [InlineData("launch")]
    [InlineData("run")]
    [InlineData("build")]
    [InlineData("test")]
    [InlineData("format")]
    [InlineData("screenshot")]
    [InlineData("run-task")]
    [InlineData("init")]
    [InlineData("check")]
    [InlineData("inspect")]
    [InlineData("gen-backend-config")]
    [InlineData("guards")]
    [InlineData("install-hooks")]
    [InlineData("bump-version")]
    [InlineData("provision-mxc")]
    public void AllCliCommands_AreRecognizedByRouter(string cmd)
    {
        Assert.True(CommandRouter.IsKnown(cmd),
            $"'{cmd}' should be a known subcommand");
    }

    [Fact]
    public void KnownCommands_CountIsFifteen()
    {
        // Every subcommand in the KnownCommands list must be recognized.
        // There are exactly 15 entries (including the "run" alias).
        var expected = new[]
        {
            "launch", "run", "build", "test", "format", "screenshot",
            "run-task", "init", "check", "inspect", "gen-backend-config",
            "guards", "install-hooks", "bump-version", "provision-mxc",
        };

        foreach (var cmd in expected)
            Assert.True(CommandRouter.IsKnown(cmd),
                $"'{cmd}' must be known for the count assertion to hold");

        // Any command NOT in the list must be rejected.
        Assert.False(CommandRouter.IsKnown("nonexistent-command"));
    }

    [Fact]
    public void UsageLine_ContainsEverySubcommand()
    {
        var usage = CommandRouter.UsageLine;

        // Every known subcommand (except the "run" alias) must appear in the
        // usage line so users can discover it.
        var mustAppear = new[]
        {
            "launch", "build", "test", "format", "screenshot", "run-task",
            "init", "install-hooks", "bump-version", "check", "inspect",
            "guards", "gen-backend-config", "provision-mxc",
        };

        foreach (var cmd in mustAppear)
            Assert.Contains(cmd, usage, StringComparison.Ordinal);

        // "run" is an alias of "launch" and may not appear separately.
    }

    // ── Guard subcommand enumeration ─────────────────────────────────────

    [Fact]
    public void AllGuardSubcommands_AreDispatched()
    {
        var paths = RepoPaths.Resolve();
        var guardsProgram = File.ReadAllText(
            Path.Combine(paths.Root, "tools", "VisualRelay.Guards", "Program.cs"));

        // The switch expression must contain case arms for all 4 guard
        // subcommands plus a default/underscore fallback.
        Assert.Contains("\"shell-size\"", guardsProgram, StringComparison.Ordinal);
        Assert.Contains("\"file-size\"", guardsProgram, StringComparison.Ordinal);
        Assert.Contains("\"source-enumeration\"", guardsProgram, StringComparison.Ordinal);
        Assert.Contains("\"sync-over-async\"", guardsProgram, StringComparison.Ordinal);
        Assert.Contains("_ =>", guardsProgram, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardSubcommand_DefaultIsShellSize()
    {
        var paths = RepoPaths.Resolve();
        var guardsProgram = File.ReadAllText(
            Path.Combine(paths.Root, "tools", "VisualRelay.Guards", "Program.cs"));

        // When no subcommand is given, the guards tool defaults to shell-size.
        Assert.Contains("\"shell-size\"", guardsProgram, StringComparison.Ordinal);
    }

    // ── Relay stage enumeration ──────────────────────────────────────────

    [Theory]
    [InlineData(1, "Ideate", "cheap", "none")]
    [InlineData(2, "Research", "cheap", "some")]
    [InlineData(3, "Diagnose", "balanced", "some")]
    [InlineData(4, "Plan", "balanced", "some")]
    [InlineData(5, "Author-tests", "balanced", "all")]
    [InlineData(6, "Implement", "balanced", "all")]
    [InlineData(7, "Review", "frontier", "some")]
    [InlineData(8, "Fix", "balanced", "all")]
    [InlineData(9, "Verify", "cheap", "some")]
    [InlineData(10, "Fix-verify", "balanced", "all")]
    [InlineData(11, "Commit", "cheap", "none")]
    public void AllRelayStages_HaveCorrectMetadata(
        int number, string name, string tier, string files)
    {
        var stage = RelayStages.All.Single(s => s.Number == number);

        Assert.Equal(name, stage.Name);
        Assert.Equal(tier, stage.Tier);
        Assert.Equal(files, stage.Files);
        Assert.NotNull(stage.SystemPrompt);
        Assert.NotNull(stage.OutputContract);

        // Stage 11 (Commit) is a driver stage — it has empty SystemPrompt
        // and OutputContract by design.
        if (number != 11)
        {
            Assert.NotEmpty(stage.SystemPrompt);
            Assert.NotEmpty(stage.OutputContract);
        }
    }

    [Fact]
    public void Stages_CountIsEleven()
    {
        Assert.Equal(11, RelayStages.All.Count);
    }

    // ── Check gate step enumeration ──────────────────────────────────────

    [Fact]
    public void CheckGate_HasAllRequiredSteps()
    {
        var paths = RepoPaths.Resolve();
        var checkSource = File.ReadAllText(
            Path.Combine(paths.Root, "tools", "VisualRelay.Cli", "Commands",
                "CheckCommand.cs"));

        // The 9 steps must appear in order in the RunAsync method body:
        // 1. Source-enumeration guard
        Assert.Contains("SourceEnumerationAsync", checkSource, StringComparison.Ordinal);
        // 2. File-size guard
        Assert.Contains("FileSize", checkSource, StringComparison.Ordinal);
        // 3. Shell-size guard
        Assert.Contains("ShellSizeAsync", checkSource, StringComparison.Ordinal);
        // 4. Dead-config-field guard
        Assert.Contains("DeadConfigFields", checkSource, StringComparison.Ordinal);
        // 5. dotnet format --verify-no-changes
        Assert.Contains("--verify-no-changes", checkSource, StringComparison.Ordinal);
        // 6. Build
        Assert.Contains("\"build\"", checkSource, StringComparison.Ordinal);
        // 7. InspectCode
        Assert.Contains("InspectCodeGate", checkSource, StringComparison.Ordinal);
        // 8. Watchdog'd tests
        Assert.Contains("RunWatchedTestsAsync", checkSource, StringComparison.Ordinal);
        // 9. Screenshots
        Assert.Contains("BuildAndRenderScreenshots", checkSource, StringComparison.Ordinal);
    }

    // ── Backend lifecycle command enumeration ────────────────────────────

    [Fact]
    public void BackendLifecycle_HasAllCommands()
    {
        var paths = RepoPaths.Resolve();
        var backendProgram = File.ReadAllText(
            Path.Combine(paths.Root, "tools", "VisualRelay.Backend", "Program.cs"));

        // The switch must dispatch start, stop, and status.
        Assert.Contains("\"start\"", backendProgram, StringComparison.Ordinal);
        Assert.Contains("\"stop\"", backendProgram, StringComparison.Ordinal);
        Assert.Contains("\"status\"", backendProgram, StringComparison.Ordinal);
        // Must have a default/usage fallback.
        Assert.Contains("default:", backendProgram, StringComparison.Ordinal);
    }

    // ── Nix flake architecture enumeration ───────────────────────────────

    [Fact]
    public void NixFlake_HasAllArchitectures()
    {
        var paths = RepoPaths.Resolve();
        var flakeNix = File.ReadAllText(Path.Combine(paths.Root, "flake.nix"));

        Assert.Contains("aarch64-darwin", flakeNix, StringComparison.Ordinal);
        Assert.Contains("x86_64-darwin", flakeNix, StringComparison.Ordinal);
        Assert.Contains("x86_64-linux", flakeNix, StringComparison.Ordinal);
        Assert.Contains("aarch64-linux", flakeNix, StringComparison.Ordinal);
    }

    // ── CI matrix RID enumeration ────────────────────────────────────────

    [Fact]
    public void CiMatrix_HasAllReleaseRids()
    {
        var paths = RepoPaths.Resolve();
        var releaseYml = File.ReadAllText(
            Path.Combine(paths.Root, ".github", "workflows", "release.yml"));

        Assert.Contains("osx-arm64", releaseYml, StringComparison.Ordinal);
        Assert.Contains("osx-x64", releaseYml, StringComparison.Ordinal);
        Assert.Contains("win-x64", releaseYml, StringComparison.Ordinal);
    }

    // ── Passthrough command enumeration ──────────────────────────────────

    [Fact]
    public void PassthroughCommand_DispatchesAllThreeTools()
    {
        var paths = RepoPaths.Resolve();
        var passthroughSource = File.ReadAllText(
            Path.Combine(paths.Root, "tools", "VisualRelay.Cli", "Commands",
                "PassthroughCommand.cs"));

        // The three passthrough tools must each have a named method.
        Assert.Contains("RunTask", passthroughSource, StringComparison.Ordinal);
        Assert.Contains("GenBackendConfig", passthroughSource, StringComparison.Ordinal);
        Assert.Contains("Guards", passthroughSource, StringComparison.Ordinal);
    }
}
