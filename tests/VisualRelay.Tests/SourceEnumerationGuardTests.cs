using VisualRelay.Core.Execution;
using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <see cref="SourceEnumerationGuard"/> — the C# port of
/// <c>tools/guards/guard-source-enumeration.sh</c>. The pre-build check detects a
/// stale virtio-fs readdir cache by comparing git-tracked sources against files
/// visible on disk under src/tests/tools (excluding bin/obj). Exit 0 when intact;
/// exit 2 when the visible count is far below the tracked count (0, or &lt; 50%).
/// </summary>
public sealed class SourceEnumerationGuardTests
{
    private static readonly IGitInvoker Git = new GitInvoker();

    /// <summary>
    /// On an intact repo, the guard exits 0 and emits no remedy message.
    /// </summary>
    [Fact]
    public async Task IntactRepo_GuardPasses()
    {
        using var repo = CreateRepoWithSources(["src/App.cs", "src/Lib.cs", "tests/App.Tests.cs"]);

        var (exitCode, message) = await SourceEnumerationGuard.RunAsync(repo.Root, Git);

        Assert.Equal(0, exitCode);
        Assert.Empty(message);
    }

    /// <summary>
    /// When 0 of the tracked source files are visible on disk, the guard
    /// exits 2 with the cause+remedy message.
    /// </summary>
    [Fact]
    public async Task ZeroVisible_GuardFailsWithRemedy()
    {
        using var repo = CreateRepoWithSources(["src/App.cs", "src/Lib.cs"]);

        // Delete visible files — simulates a fully stale readdir cache.
        foreach (var f in Directory.GetFiles(repo.Root, "*.cs", SearchOption.AllDirectories))
        {
            File.Delete(f);
        }

        var (exitCode, message) = await SourceEnumerationGuard.RunAsync(repo.Root, Git);

        Assert.Equal(2, exitCode);
        Assert.Contains("STALE VIRTIO-FS", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("READDIR CACHE", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("claude-vm/fix-cache.sh", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("diskutil unmount", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rm -rf obj bin", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the visible count is below the 50% threshold, the guard exits 2
    /// and reports the percentage.
    /// </summary>
    [Fact]
    public async Task PartialVisible_BelowThreshold_GuardFails()
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

        var (exitCode, message) = await SourceEnumerationGuard.RunAsync(repo.Root, Git);

        Assert.Equal(2, exitCode);
        Assert.Contains("STALE VIRTIO-FS", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%", message, StringComparison.Ordinal); // reports the percentage
        Assert.Contains("claude-vm/fix-cache.sh", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// When the visible count is at or above the 50% threshold, the guard
    /// passes (false positives are worse than false negatives for this check).
    /// </summary>
    [Fact]
    public async Task PartialVisible_AboveThreshold_GuardPasses()
    {
        // 4 tracked, delete 1 → 3 visible (75%) → above 50% threshold.
        using var repo = CreateRepoWithSources([
            "src/A.cs", "src/B.cs", "src/C.cs", "src/D.cs"
        ]);

        var allFiles = Directory.GetFiles(repo.Root, "*.cs", SearchOption.AllDirectories);
        File.Delete(allFiles[0]); // delete 1

        var (exitCode, _) = await SourceEnumerationGuard.RunAsync(repo.Root, Git);

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// The guard excludes obj/ and bin/ directories from the visible count,
    /// matching the existing check-file-size.sh convention.
    /// </summary>
    [Fact]
    public async Task ExcludesObjAndBinFromVisibleCount()
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

        var (exitCode, _) = await SourceEnumerationGuard.RunAsync(repo.Root, Git);

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// The guard covers .axaml files in addition to .cs files, since MSBuild
    /// also globs them implicitly and they are affected by the same readdir bug.
    /// </summary>
    [Fact]
    public async Task CoversAxamlFiles()
    {
        using var repo = CreateRepoWithSources(["src/App.cs", "src/MainWindow.axaml"]);

        // Delete both — guard should report 0 visible, not just .cs.
        foreach (var f in Directory.GetFiles(repo.Root, "*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs") || f.EndsWith(".axaml")))
        {
            File.Delete(f);
        }

        var (exitCode, message) = await SourceEnumerationGuard.RunAsync(repo.Root, Git);

        Assert.Equal(2, exitCode);
        Assert.Contains("STALE VIRTIO-FS", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Creates a temp git repo with the given relative file paths under
    /// <c>src/</c> (and optionally <c>tests/</c>, <c>tools/</c>), commits them,
    /// and returns a disposable wrapper.
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

    /// <summary>Wraps <see cref="TestRepository"/> for disposal.</summary>
    private sealed class TestRepo(TestRepository repo) : IDisposable
    {
        public string Root => repo.Root;

        public void Dispose() => repo.Dispose();
    }
}
