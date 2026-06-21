using VisualRelay.Guards;

namespace VisualRelay.Cli.Gates;

/// <summary>
/// The InspectCode zero-findings gate (ports <c>tools/guards/inspect-code.sh</c>).
/// Restores the JetBrains tool, runs <c>dotnet jb inspectcode</c> over the solution
/// at the SUGGESTION floor writing SARIF under XDG cache, then gates on zero
/// <c>runs[].results[]</c> via <see cref="InspectCodeSarifParser"/>. InspectCode
/// always exits 0 — the SARIF is the sole source of truth. Returns 0 when clean,
/// 1 when findings remain, and the tool's own exit code on a restore/run failure.
/// </summary>
public static class InspectCodeGate
{
    public static int Run(RepoPaths paths)
    {
        var cacheRoot = CacheRoot();
        var cachesHome = Path.Combine(cacheRoot, "caches");
        var sarifPath = Path.Combine(cacheRoot, "inspectcode.sarif.json");
        Directory.CreateDirectory(cachesHome);

        Console.Error.WriteLine($"inspect-code: restoring dotnet tools from {paths.ToolManifest}");
        var restore = ProcessLauncher.Run(ProcessLauncher.Dotnet,
            ["tool", "restore", "--tool-manifest", paths.ToolManifest], paths.Root);
        if (restore != 0)
            return restore;

        Console.Error.WriteLine("inspect-code: running JetBrains InspectCode (floor: SUGGESTION)");
        var run = ProcessLauncher.Run(ProcessLauncher.Dotnet,
            [
                "jb", "inspectcode", paths.Solution,
                "--no-build",
                $"--output={sarifPath}",
                $"--caches-home={cachesHome}",
                "--severity=SUGGESTION",
                "--format=Sarif",
            ],
            paths.Root);
        if (run != 0)
            return run;

        return Gate(sarifPath);
    }

    private static int Gate(string sarifPath)
    {
        if (!File.Exists(sarifPath))
        {
            Console.Error.WriteLine($"inspect-code: SARIF not produced at {sarifPath}");
            return 1;
        }

        var count = InspectCodeSarifParser.CountResults(File.ReadAllText(sarifPath));
        if (count == 0)
        {
            Console.Error.WriteLine("inspect-code: 0 findings — gate passed.");
            return 0;
        }

        Console.Error.WriteLine($"inspect-code: {count} finding(s) at or above SUGGESTION floor.");
        Console.Error.WriteLine($"SARIF: {sarifPath}");
        Console.Error.WriteLine("Review each finding.  Fix real defects in code; only suppress via .editorconfig");
        Console.Error.WriteLine("with a documented rationale.  Never carve out a real defect.");
        return 1;
    }

    private static string CacheRoot()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var baseDir = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache")
            : xdg;
        return Path.Combine(baseDir, "visual-relay", "inspectcode");
    }
}
