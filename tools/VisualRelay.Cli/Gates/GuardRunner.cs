using VisualRelay.Core.Execution;
using VisualRelay.Guards;

namespace VisualRelay.Cli.Gates;

/// <summary>
/// In-process adapters for the C# guards (ports of the retired
/// <c>tools/guards/*.sh</c>). The Cli <c>build</c>/<c>check</c> commands call these
/// instead of shelling out to bash; each writes the guard's diagnostics to stderr
/// and returns the script-compatible exit code (source-enum: 0/2, file-size and
/// shell-size: 0/1).
/// </summary>
public static class GuardRunner
{
    /// <summary>
    /// Runs <see cref="SourceEnumerationGuard"/> against the repo root. Returns 0
    /// when the on-disk view is intact, 2 on a stale virtio-fs/readdir cache.
    /// </summary>
    public static async Task<int> SourceEnumerationAsync(RepoPaths paths)
    {
        var (exitCode, message) = await SourceEnumerationGuard.RunAsync(paths.Root, new GitInvoker());
        if (exitCode != 0)
            Console.Error.WriteLine(message);
        return exitCode;
    }

    /// <summary>
    /// Runs <see cref="FileSizeGuard"/> over src/tests/tools at the env-resolved
    /// limit (default 300). Returns 0 when every file is within the limit, 1
    /// otherwise (printing each over-limit file to stderr).
    /// </summary>
    public static int FileSize(RepoPaths paths)
    {
        var limit = FileSizeGuard.ResolveLimit();
        var violations = FileSizeGuard.Enumerate(paths.Root, ["src", "tests", "tools"], limit);
        foreach (var v in violations)
            Console.Error.WriteLine($"file too large: {v.Path} has {v.Lines} lines (limit {v.Limit})");
        return violations.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// Runs <see cref="ShellSizeGuard"/> over every git-tracked shell script at the
    /// env-resolved limit (default 24; the <c>visual-relay</c> bootstrap has a
    /// fixed 100-line carve-out). Also runs <see cref="ShellFormatGuard"/> to
    /// verify shfmt formatting. Returns 0 when every script is within its limit
    /// AND formatting is clean, 1 otherwise (printing over-limit scripts and diffs
    /// to stderr). The authoritative gate is the <c>ShellScriptSizeGuardTests</c>
    /// guard-as-test; this is the same size check plus the format verification run
    /// as a fast pre-build step so <c>check</c> fails early.
    /// </summary>
    public static async Task<int> ShellSizeAsync(RepoPaths paths)
    {
        List<(string Path, string[] Lines)> tracked;
        try
        {
            tracked = await TrackedShellScripts.EnumerateAsync(paths.Root, new GitInvoker());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"shell-size: {ex.Message}");
            return 1;
        }

        var violations = ShellSizeGuard.FindViolations(tracked, ShellSizeGuard.ResolveLimit());
        foreach (var v in violations)
            Console.Error.WriteLine($"shell too large: {v.Path} has {v.Count} logic lines (limit {v.Limit})");

        var formatPaths = tracked.Select(t => t.Path).ToList();
        var formatResult = await ShellFormatGuard.CheckAsync(paths.Root, formatPaths, CancellationToken.None);

        if (!formatResult.Clean)
        {
            if (formatResult.Error is not null)
            {
                Console.Error.WriteLine($"shell-format: {formatResult.Error}");
            }
            else if (formatResult.Output is not null)
            {
                Console.Error.Write(formatResult.Output);
                Console.Error.WriteLine("  → run ./visual-relay format");
            }
        }

        return violations.Count > 0 || !formatResult.Clean ? 1 : 0;
    }

    /// <summary>
    /// Runs <see cref="DeadConfigFieldGuard"/> with CANDIDATES from <c>src/</c> (the config
    /// record + loader) and CONSUMERS from <c>src/</c> + <c>tools/</c> (product code;
    /// bin/obj excluded). <c>tests/</c> is intentionally not a consumer source — a field
    /// used only by tests is effectively dead in the product. Returns 0 when every config
    /// field the loader parses has a consumer, 1 otherwise (printing each dead field to
    /// stderr). Catches config knobs that are parsed-but-consumed-nowhere — which InspectCode
    /// structurally cannot see, because <c>RelayConfigLoader</c> reads each field as its own
    /// fallback default (<c>defaults.Field</c>), a phantom self-read. The authoritative gate
    /// is the <c>DeadConfigFieldGuardTests</c> guard-as-test; this is the same check run as a
    /// fast pre-build step so <c>check</c> fails early.
    /// </summary>
    public static int DeadConfigFields(RepoPaths paths)
    {
        var candidateFiles = EnumerateCs(paths, "src");
        var consumerFiles = EnumerateCs(paths, "src", "tools");

        var violations = DeadConfigFieldGuard.FindViolations(candidateFiles, consumerFiles);
        foreach (var v in violations)
            Console.Error.WriteLine($"dead config field: {v.Path}:{v.Line}: {v.Field} — {v.Reason}");
        return violations.Count > 0 ? 1 : 0;
    }

    /// <summary>Reads every non-build-artifact <c>*.cs</c> under the given repo-relative dirs as (relPath, source) pairs.</summary>
    private static List<(string Path, string Source)> EnumerateCs(RepoPaths paths, params string[] dirs) =>
        dirs.SelectMany(d => Directory.EnumerateFiles(Path.Combine(paths.Root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildArtifact(f))
            .Select(f => (Path.GetRelativePath(paths.Root, f), File.ReadAllText(f)))
            .ToList();

    /// <summary>True when the path lives under a <c>bin</c> or <c>obj</c> build-output segment.</summary>
    private static bool IsBuildArtifact(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj");
    }
}
