using VisualRelay.Core.Execution;

namespace VisualRelay.Guards;

/// <summary>
/// C# port of <c>tools/guards/guard-source-enumeration.sh</c>. Detects a stale
/// virtio-fs / readdir cache on the dev VM: when directory listings return empty
/// (or a subset), MSBuild's default <c>**/*.cs</c> glob silently compiles only the
/// files it can see. The guard compares the count of git-tracked <c>*.cs</c>/<c>*.axaml</c>
/// against the count visible on disk under src/tests/tools (excluding bin/obj) and
/// fails the build when visible is drastically below tracked (0, or &lt; 50%).
/// </summary>
public static class SourceEnumerationGuard
{
    /// <summary>Patterns MSBuild SDK projects glob implicitly; *.cs is the critical one.</summary>
    private static readonly string[] Patterns = ["*.cs", "*.axaml"];

    /// <summary>Source/tool/test roots scanned for visible files.</summary>
    private static readonly string[] ScanDirs = ["src", "tests", "tools"];

    /// <summary>Visible-to-tracked fraction below which the build is blocked.</summary>
    private const double MinRatio = 0.50;

    /// <summary>
    /// Runs the guard against <paramref name="repoRoot"/>: counts git-tracked
    /// sources (via <paramref name="git"/>) and visible-on-disk sources, then
    /// applies the ratio rule. Returns the exit code (0 ok, 2 stale) and a
    /// cause+remedy message (empty when ok).
    /// </summary>
    public static async Task<(int ExitCode, string Message)> RunAsync(string repoRoot, IGitInvoker git)
    {
        var tracked = await CountTrackedAsync(repoRoot, git);
        var visible = CountVisible(repoRoot);
        return Evaluate(tracked, visible);
    }

    /// <summary>
    /// Pure decision over precomputed counts. Mirrors the script: tracked==0 ⇒ ok;
    /// visible==0 ⇒ stale (no-files message); ratio &lt; <see cref="MinRatio"/> ⇒
    /// stale (percentage message); otherwise ok.
    /// </summary>
    private static (int ExitCode, string Message) Evaluate(int trackedTotal, int visibleTotal)
    {
        if (trackedTotal == 0)
            return (0, string.Empty);

        if (visibleTotal == 0)
            return (2, StaleMessage(ZeroVisibleDetail(trackedTotal)));

        var ratio = (double)visibleTotal / trackedTotal;
        if (ratio < MinRatio)
        {
            var pct = (int)Math.Round(ratio * 100);
            return (2, StaleMessage(BelowThresholdDetail(trackedTotal, visibleTotal, pct)));
        }

        return (0, string.Empty);
    }

    /// <summary>Counts git-tracked files matching <see cref="Patterns"/>.</summary>
    private static async Task<int> CountTrackedAsync(string repoRoot, IGitInvoker git)
    {
        var total = 0;
        foreach (var pattern in Patterns)
        {
            var (exit, output, timedOut) =
                await git.RunAsync(repoRoot, ["ls-files", pattern], CancellationToken.None);
            if (exit != 0 || timedOut)
                continue;
            total += output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        }

        return total;
    }

    /// <summary>
    /// Counts files matching <see cref="Patterns"/> visible on disk under
    /// <see cref="ScanDirs"/> (missing dirs skipped), excluding bin/obj.
    /// </summary>
    private static int CountVisible(string repoRoot)
    {
        var total = 0;
        foreach (var dir in ScanDirs)
        {
            var dirPath = Path.Combine(repoRoot, dir);
            if (!Directory.Exists(dirPath))
                continue;

            foreach (var pattern in Patterns)
            {
                foreach (var file in Directory.EnumerateFiles(dirPath, pattern, SearchOption.AllDirectories))
                {
                    if (!IsBuildArtifact(file))
                        total++;
                }
            }
        }

        return total;
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

    private static string ZeroVisibleDetail(int tracked) =>
        $"git tracks {tracked} source file(s) across {string.Join(' ', Patterns)}, but 0 files are\n" +
        "visible on disk (excluding obj/ / bin/).";

    private static string BelowThresholdDetail(int tracked, int visible, int pct) =>
        $"git tracks {tracked} source file(s) across {string.Join(' ', Patterns)}, but only\n" +
        $"{visible} are visible on disk (~{pct} % of tracked, below the 50% threshold).";

    private static string StaleMessage(string detail) =>
        "guard-source-enumeration: STALE VIRTIO-FS / READDIR CACHE DETECTED\n\n" +
        detail + "\n\n" +
        "MSBuild's default **/*.cs glob enumerates via readdir.  When the guest's\n" +
        "directory cache is stale — a known virtio-fs bug on Tart VMs — readdir\n" +
        "returns empty/incomplete results, so the project silently compiles ZERO or a\n" +
        "SUBSET of its sources into an empty/partial assembly.  This causes cryptic\n" +
        "CS0234 cascades downstream.\n\n" +
        "FIX: remount the shared filesystem.  From the host (macOS):\n" +
        "  - Run:  claude-vm/fix-cache.sh\n" +
        "  - Or:   sudo diskutil unmount <mount-path>\n" +
        "           sudo mount -t virtiofs <tag> <mount-path>\n" +
        "  - Or restart the VM entirely.\n\n" +
        "NOTE: rm -rf obj bin will NOT help — the filesystem cache is in the guest\n" +
        "kernel, not on disk.  The files exist and read fine by name; only directory\n" +
        "enumeration is broken.";
}
