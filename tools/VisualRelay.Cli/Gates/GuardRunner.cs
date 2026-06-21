using VisualRelay.Core.Execution;
using VisualRelay.Guards;

namespace VisualRelay.Cli.Gates;

/// <summary>
/// In-process adapters for the C# guards (ports of the retired
/// <c>tools/guards/*.sh</c>). The Cli <c>build</c>/<c>check</c> commands call these
/// instead of shelling out to bash; each writes the guard's diagnostics to stderr
/// and returns the script-compatible exit code (source-enum: 0/2, file-size: 0/1).
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
}
