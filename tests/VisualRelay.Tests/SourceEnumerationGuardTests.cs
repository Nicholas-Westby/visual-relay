using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <c>tools/guards/guard-source-enumeration.sh</c> —
/// the pre-build check that detects a stale virtio-fs readdir cache
/// by comparing git-tracked sources against files visible on disk.
/// </summary>
public sealed class SourceEnumerationGuardTests
{
    private static string GuardScriptPath =>
        Path.Combine(RepoSetup.Root, "tools", "guards", "guard-source-enumeration.sh");

    /// <summary>
    /// On an intact repo, the guard exits 0 and the visible count matches
    /// the git-tracked count.
    /// </summary>
    [Fact]
    public void IntactRepo_GuardPasses()
    {
        using var repo = CreateRepoWithSources(["src/App.cs", "src/Lib.cs", "tests/App.Tests.cs"]);

        var (exitCode, stderr) = RunGuard(repo.Root);

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
    }

    /// <summary>
    /// When 0 of the tracked source files are visible on disk, the guard
    /// exits 2 with the cause+remedy message.
    /// </summary>
    [Fact]
    public void ZeroVisible_GuardFailsWithRemedy()
    {
        using var repo = CreateRepoWithSources(["src/App.cs", "src/Lib.cs"]);

        // Delete visible files — simulates a fully stale readdir cache.
        foreach (var f in Directory.GetFiles(repo.Root, "*.cs", SearchOption.AllDirectories))
        {
            File.Delete(f);
        }

        var (exitCode, stderr) = RunGuard(repo.Root);

        Assert.Equal(2, exitCode);
        Assert.Contains("STALE VIRTIO-FS", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("READDIR CACHE", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude-vm/fix-cache.sh", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("diskutil unmount", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rm -rf obj bin", stderr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the visible count is below the 50% threshold, the guard exits 2
    /// and reports the percentage.
    /// </summary>
    [Fact]
    public void PartialVisible_BelowThreshold_GuardFails()
    {
        // 6 tracked, delete 5 → 1 visible (16%) → below 50% threshold.
        using var repo = CreateRepoWithSources([
            "src/A.cs", "src/B.cs", "src/C.cs",
            "src/D.cs", "src/E.cs", "src/F.cs"
        ]);

        var allFiles = Directory.GetFiles(repo.Root, "*.cs", SearchOption.AllDirectories);
        // Delete all but the first.
        foreach (var f in allFiles.Skip(1))
        {
            File.Delete(f);
        }

        var (exitCode, stderr) = RunGuard(repo.Root);

        Assert.Equal(2, exitCode);
        Assert.Contains("STALE VIRTIO-FS", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%", stderr, StringComparison.Ordinal); // reports the percentage
        Assert.Contains("claude-vm/fix-cache.sh", stderr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the visible count is at or above the 50% threshold, the guard
    /// passes (false positives are worse than false negatives for this check).
    /// </summary>
    [Fact]
    public void PartialVisible_AboveThreshold_GuardPasses()
    {
        // 4 tracked, delete 1 → 3 visible (75%) → above 50% threshold.
        using var repo = CreateRepoWithSources([
            "src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs"
        ]);

        var allFiles = Directory.GetFiles(repo.Root, "*.cs", SearchOption.AllDirectories);
        File.Delete(allFiles[0]); // delete 1

        var (exitCode, _) = RunGuard(repo.Root);

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// The guard excludes obj/ and bin/ directories from the visible count,
    /// matching the existing check-file-size.sh convention.
    /// </summary>
    [Fact]
    public void ExcludesObjAndBinFromVisibleCount()
    {
        using var repo = CreateRepoWithSources(["src/App.cs"]);

        // Create obj/ and bin/ directories with .cs files — these should be
        // ignored by the guard (they are build artifacts, not sources).
        var objDir = Path.Combine(repo.Root, "src", "obj", "Debug");
        var binDir = Path.Combine(repo.Root, "src", "bin", "Debug");
        Directory.CreateDirectory(objDir);
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(objDir, "generated.cs"), "// generated");
        File.WriteAllText(Path.Combine(binDir, "leftover.cs"), "// leftover");

        var (exitCode, _) = RunGuard(repo.Root);

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// The guard covers .axaml files in addition to .cs files, since MSBuild
    /// also globs them implicitly and they are affected by the same readdir bug.
    /// </summary>
    [Fact]
    public void CoversAxamlFiles()
    {
        using var repo = CreateRepoWithSources(["src/App.cs", "src/MainWindow.axaml"]);

        // Delete both — guard should report 0 visible, not just .cs.
        foreach (var f in Directory.GetFiles(repo.Root, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs") || f.EndsWith(".axaml")))
        {
            File.Delete(f);
        }

        var (exitCode, stderr) = RunGuard(repo.Root);

        Assert.Equal(2, exitCode);
        Assert.Contains("STALE VIRTIO-FS", stderr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a temp git repo under <c>src/</c> (and optionally
    /// <c>tests/</c>, <c>tools/</c>) with the given relative file paths,
    /// commits them, and returns a disposable wrapper.
    /// </summary>
    private static TestRepo CreateRepoWithSources(string[] files)
    {
        var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "guard-test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Guard Test");

        foreach (var relPath in files)
        {
            var fullPath = Path.Combine(repo.Root, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, $"// {relPath}");
        }

        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "chore: seed test sources");

        return new TestRepo(repo);
    }

    /// <summary>
    /// Runs the guard script in the context of the given repo root.
    /// The script resolves the repo from its own path, so we symlink or
    /// copy it into the temp repo's <c>tools/guards/</c> so it walks the
    /// right tree.
    /// </summary>
    private static (int ExitCode, string Stderr) RunGuard(string repoRoot)
    {
        // Place a copy of the guard script in the fixture repo so it
        // resolves repo_root as the fixture root (tools/guards/ → ../..).
        var guardDir = Path.Combine(repoRoot, "tools", "guards");
        Directory.CreateDirectory(guardDir);
        var destScript = Path.Combine(guardDir, "guard-source-enumeration.sh");
        File.Copy(GuardScriptPath, destScript, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destScript,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var startInfo = new ProcessStartInfo("bash")
        {
            ArgumentList = { destScript },
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(milliseconds: 10_000);
        var stderr = process.StandardError.ReadToEnd();
        return (process.ExitCode, stderr);
    }

    /// <summary>
    /// Wraps <see cref="TestRepository"/> to guarantee the guard script copy
    /// (which may be read-only after chmod) is cleaned up on disposal.
    /// </summary>
    private sealed class TestRepo : IDisposable
    {
        private readonly TestRepository _repo;

        public TestRepo(TestRepository repo) => _repo = repo;

        public string Root => _repo.Root;

        public void Dispose()
        {
            // Best-effort: ensure copied scripts are deletable on Unix.
            var guardDir = Path.Combine(_repo.Root, "tools", "guards");
            if (Directory.Exists(guardDir) && !OperatingSystem.IsWindows())
            {
                foreach (var f in Directory.GetFiles(guardDir))
                {
                    try
                    {
                        File.SetUnixFileMode(f,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch
                    {
                        // Best-effort.
                    }
                }
            }

            _repo.Dispose();
        }
    }
}
