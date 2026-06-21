namespace VisualRelay.Guards;

/// <summary>
/// CLI runner for <see cref="FileSizeGuard"/> (ports <c>check-file-size.sh</c>):
/// scans src/tests/tools <c>*.cs</c>/<c>*.axaml</c> at the env-resolved limit and
/// exits 1 (printing each over-limit file to stderr) when any file exceeds it.
/// </summary>
public static class FileSizeGuardRunner
{
    public static int Run(string repoRoot)
    {
        var limit = FileSizeGuard.ResolveLimit();
        var violations = FileSizeGuard.Enumerate(repoRoot, ["src", "tests", "tools"], limit);
        foreach (var v in violations)
            Console.Error.WriteLine($"file too large: {v.Path} has {v.Lines} lines (limit {v.Limit})");
        return violations.Count > 0 ? 1 : 0;
    }
}
