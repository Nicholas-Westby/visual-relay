namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>check</c>: the full inner-loop gate, preserving the launcher's order —
/// source-enum guard → file-size guard → shell-size guard → dead-config-field guard →
/// <c>dotnet format --verify-no-changes</c> → build → InspectCode → watchdog'd test
/// → screenshots. Any step failing short-circuits with its exit code. The test step
/// uses VISUAL_RELAY_CHECK_TEST_TIMEOUT (default 300s) so a deadlocked suite is
/// capped. The shell-size guard is also enforced authoritatively by the
/// ShellScriptSizeGuardTests guard-as-test in the test step.
/// </summary>
public static class CheckCommand
{
    public static async Task<int> RunAsync(RepoPaths paths)
    {
        var rc = await Gates.GuardRunner.SourceEnumerationAsync(paths);
        if (rc != 0) return rc;

        rc = Gates.GuardRunner.FileSize(paths);
        if (rc != 0) return rc;

        rc = await Gates.GuardRunner.ShellSizeAsync(paths);
        if (rc != 0) return rc;

        rc = Gates.GuardRunner.DeadConfigFields(paths);
        if (rc != 0) return rc;

        rc = ProcessLauncher.Run(ProcessLauncher.Dotnet, ["format", paths.Solution, "--verify-no-changes"], paths.Root);
        if (rc != 0) return rc;

        rc = ProcessLauncher.Run(ProcessLauncher.Dotnet,
            ["build", paths.Solution, "-m:1", "-p:UseSharedCompilation=false"], paths.Root);
        if (rc != 0) return rc;

        rc = Gates.InspectCodeGate.Run(paths);
        if (rc != 0) return rc;

        rc = await RunWatchedTestsAsync(paths);
        if (rc != 0) return rc;

        return BuildAndRenderScreenshots(paths);
    }

    private static async Task<int> RunWatchedTestsAsync(RepoPaths paths)
    {
        var timeout = WatchdogTimeouts.ForCheck();
        var testArgs = new[]
        {
            "test", paths.TestsProject, "-m:1", "-p:UseSharedCompilation=false",
        };
        var rc = await TimeoutWatchdog.RunAsync(ProcessLauncher.Dotnet, testArgs, paths.Root, timeout);
        if (rc == 124)
        {
            Console.Error.WriteLine($"visual-relay: test timed out after {timeout.TotalSeconds:F0}s");
            Console.Error.WriteLine(
                "  To find which test is stuck: ./visual-relay test --blame-hang --blame-hang-timeout 30s");
        }
        return rc;
    }

    private static int BuildAndRenderScreenshots(RepoPaths paths)
    {
        var proj = paths.ToolProject("VisualRelay.Screenshots");

        var build = ProcessLauncher.Run(ProcessLauncher.Dotnet,
            ["build", proj, "-m:1", "-p:UseSharedCompilation=false"], paths.Root);
        if (build != 0) return build;

        var main = ProcessLauncher.Run(ProcessLauncher.Dotnet,
            ["run", "--no-build", "--project", proj, "--", paths.DocsImage("visual-relay-main.png")],
            paths.Root);
        if (main != 0) return main;

        return ProcessLauncher.Run(ProcessLauncher.Dotnet,
            ["run", "--no-build", "--project", proj, "--", paths.DocsImage("visual-relay-compact.png"), "1060", "720"],
            paths.Root);
    }
}
