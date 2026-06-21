namespace VisualRelay.Guards;

/// <summary>
/// C# port of <c>tools/guards/check-file-size.sh</c>. Flags <c>*.cs</c>/<c>*.axaml</c>
/// files under the source roots whose line count exceeds a limit (default 300,
/// caller-supplied), excluding <c>bin/</c>/<c>obj/</c> directories. Line counting
/// mirrors the established C# convention (<see cref="File.ReadAllLines(string)"/>
/// length), matching <c>SplitGuardVerificationTests</c>.
/// </summary>
public static class FileSizeGuard
{
    /// <summary>The default per-file line limit.</summary>
    private const int DefaultLimit = 300;

    /// <summary>The environment variable that overrides <see cref="DefaultLimit"/>.</summary>
    private const string LimitEnvVar = "VISUAL_RELAY_FILE_LINE_LIMIT";

    /// <summary>Source file extensions the guard scans (case-insensitive).</summary>
    private static readonly string[] Extensions = [".cs", ".axaml"];

    /// <summary>Describes a single over-limit file.</summary>
    public sealed record Violation(string Path, int Lines, int Limit);

    /// <summary>
    /// Pure decision: returns the over-limit files from precomputed
    /// <paramref name="files"/> (relative path + line count), ordered by path.
    /// </summary>
    public static IReadOnlyList<Violation> FindViolations(
        IReadOnlyList<(string Path, int Lines)> files,
        int limit)
    {
        var violations = new List<Violation>();
        foreach (var (path, lines) in files)
        {
            if (lines > limit)
                violations.Add(new Violation(path, lines, limit));
        }

        violations.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return violations;
    }

    /// <summary>
    /// Enumerates <c>*.cs</c>/<c>*.axaml</c> under each of <paramref name="roots"/>
    /// (relative to <paramref name="repoRoot"/>, missing roots skipped), excluding
    /// <c>bin/</c>/<c>obj/</c>, counts lines, and returns the over-limit files with
    /// repo-relative paths.
    /// </summary>
    public static IReadOnlyList<Violation> Enumerate(string repoRoot, IReadOnlyList<string> roots, int limit)
    {
        var files = new List<(string Path, int Lines)>();
        foreach (var root in roots)
        {
            var rootPath = Path.Combine(repoRoot, root);
            if (!Directory.Exists(rootPath))
                continue;

            foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
            {
                if (!HasSourceExtension(file) || IsBuildArtifact(file))
                    continue;

                var rel = Path.GetRelativePath(repoRoot, file);
                files.Add((rel, File.ReadAllLines(file).Length));
            }
        }

        return FindViolations(files, limit);
    }

    /// <summary>Resolves the limit from the environment, falling back to <see cref="DefaultLimit"/>.</summary>
    public static int ResolveLimit()
    {
        var env = Environment.GetEnvironmentVariable(LimitEnvVar);
        return int.TryParse(env, out var parsed) ? parsed : DefaultLimit;
    }

    private static bool HasSourceExtension(string path)
    {
        var ext = Path.GetExtension(path);
        foreach (var candidate in Extensions)
        {
            if (string.Equals(ext, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsBuildArtifact(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        var alt = Path.AltDirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
            || path.Contains($"{sep}obj{sep}", StringComparison.Ordinal)
            || path.Contains($"{alt}bin{alt}", StringComparison.Ordinal)
            || path.Contains($"{alt}obj{alt}", StringComparison.Ordinal);
    }
}
