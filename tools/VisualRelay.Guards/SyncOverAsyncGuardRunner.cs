namespace VisualRelay.Guards;

/// <summary>
/// CLI runner for <see cref="SyncOverAsyncGuard"/>: walks
/// <c>tests/VisualRelay.Tests/**/*.cs</c> (excluding bin/obj), calls
/// <see cref="SyncOverAsyncGuard.FindViolations"/>, prints violations to stderr,
/// and exits 1 when any are found.
/// </summary>
public static class SyncOverAsyncGuardRunner
{
    public static int Run(string repoRoot)
    {
        var testsDir = Path.Combine(repoRoot, "tests", "VisualRelay.Tests");

        if (!Directory.Exists(testsDir))
        {
            Console.Error.WriteLine($"sync-over-async guard: directory not found: {testsDir}");
            return 1;
        }

        var files = Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildArtifact(f))
            .Select(f => (Path.GetRelativePath(repoRoot, f), File.ReadAllText(f)))
            .ToList();

        var violations = SyncOverAsyncGuard.FindViolations(files);

        foreach (var v in violations)
            Console.Error.WriteLine($"{v.Path}:{v.Line}: {v.Snippet} — {v.Reason}");

        if (violations.Count > 0)
        {
            Console.Error.WriteLine($"sync-over-async guard: {violations.Count} violation(s) found.");
            return 1;
        }

        return 0;
    }

    /// <summary>True when the path lives under a <c>bin</c> or <c>obj</c> build-output segment.</summary>
    private static bool IsBuildArtifact(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => s is "bin" or "obj");
    }
}
