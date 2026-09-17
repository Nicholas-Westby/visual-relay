namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>check</c>: the full inner-loop gate, preserving the launcher's order —
/// source-enum guard → file-size guard → shell-size guard (size + shfmt format
/// verification) → dead-config-field guard → <c>dotnet format --verify-no-changes</c>
/// → build → InspectCode → watchdog'd test → screenshot determinism check. Any step failing
/// short-circuits with its exit code. The test step uses VISUAL_RELAY_CHECK_TEST_TIMEOUT
/// (default 300s) so a deadlocked suite is capped. The shell-size guard is also
/// enforced authoritatively by the ShellScriptSizeGuardTests guard-as-test in the
/// test step; shell formatting is enforced here via shfmt --diff.
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
                "  To find which test is stuck: ./visual-relay test --blame-hang --blame-hang-timeout 120s --blame-hang-dump-type none");
        }
        return rc;
    }

    private static int BuildAndRenderScreenshots(RepoPaths paths)
    {
        var proj = paths.ToolProject("VisualRelay.Screenshots");

        var build = ProcessLauncher.Run(ProcessLauncher.Dotnet,
            ["build", proj, "-m:1", "-p:UseSharedCompilation=false"], paths.Root);
        if (build != 0) return build;

        var (exitCode, message) = RenderAndCompare(ScreenshotCheckDir(paths.Root),
            (output, width, height) => ProcessLauncher.Run(ProcessLauncher.Dotnet,
                ["run", "--no-build", "--project", proj, "--", output, $"{width}", $"{height}"],
                paths.Root));
        if (message is not null) Console.Error.WriteLine(message);
        return exitCode;
    }

    /// <summary>
    /// Where the gate renders. Deliberately not <c>docs/images/</c>: rendering
    /// there modified both committed PNGs on every run, so a <c>check</c> on a
    /// clean tree ended dirty and the images drifted to whatever was last left
    /// unreverted. <c>./visual-relay screenshot</c> is the deliberate refresh.
    /// </summary>
    internal static string ScreenshotCheckDir(string root) =>
        Path.Combine(root, ".relay", "scratch", "check-screenshots");

    /// <summary>
    /// Renders the main window twice and the compact size once, and reports a
    /// message when the two main renders are not byte-identical. That is the
    /// runtime proof that the render is a pure function of the commit — the only
    /// thing the gate can assert without pinning the images themselves, which
    /// would fail every intentional UI change instead of asking for a refresh.
    /// </summary>
    internal static (int ExitCode, string? Message) RenderAndCompare(
        string outDir, ScreenshotRender render)
    {
        Directory.CreateDirectory(outDir);

        var first = Path.Combine(outDir, "main.png");
        var main = render(first, 1440, 900);
        if (main != 0) return (main, null);

        var second = Path.Combine(outDir, "main-again.png");
        main = render(second, 1440, 900);
        if (main != 0) return (main, null);

        var differing = DifferingByteCount(File.ReadAllBytes(first), File.ReadAllBytes(second));
        if (differing > 0)
        {
            return (1,
                $"visual-relay: the screenshot render is not deterministic ({differing} bytes differ); "
                + "something in the UI reads the clock or the machine");
        }

        return (render(Path.Combine(outDir, "compact.png"), 1060, 720), null);
    }

    private static int DifferingByteCount(byte[] first, byte[] second)
    {
        var shared = Math.Min(first.Length, second.Length);
        var differing = Math.Abs(first.Length - second.Length);
        for (var i = 0; i < shared; i++)
        {
            if (first[i] != second[i]) differing++;
        }
        return differing;
    }
}

/// <summary>Renders one screenshot of the given size and returns the exit code.</summary>
internal delegate int ScreenshotRender(string output, int width, int height);
