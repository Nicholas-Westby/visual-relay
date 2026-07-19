using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed partial class WorktreeFilterTests
{
    /// <summary>
    /// Helper: registers an in-memory GitSim repo rooted at <paramref name="root"/>
    /// with a single tracked file committed, then returns the absolute path to that
    /// file. GitSim state lives in a process-wide per-root registry, so callers
    /// needing a git invoker to pass into the code under test just construct a
    /// fresh <see cref="GitSimEngine"/> — it resolves the same registered repo.
    /// </summary>
    private static Task<string> InitRepoWithTrackedFile(string root, string relPath, string content)
    {
        var sim = new GitSimEngine();
        sim.InitRepo(root);
        sim.Seed(root, relPath, content);
        sim.Commit(root, "seed");
        return Task.FromResult(Path.Combine(root, relPath));
    }

    /// <summary>
    /// GitSim GAP compensation (verified empirically — not a modeling choice): GitSim's
    /// <c>checkout &lt;rev&gt; -- &lt;path&gt;</c> only visits paths that ARE present in the
    /// resolved revision's tree, so for a path ABSENT from that tree it loops zero times
    /// and still returns exit 0 — whereas real git fails ("error: pathspec '...' did not
    /// match any file(s) known to git", non-zero exit). <see cref="WorktreeFilter"/>'s
    /// revert loop depends on that real-git failure to detect "genuinely absent from
    /// HEAD" (it then probes <c>cat-file -e</c>, which GitSim DOES implement correctly,
    /// before unstaging + deleting) — so every fact that reverts a STAGED-BUT-NEVER-
    /// COMMITTED path needs this correction or the production code silently no-ops
    /// instead of unstaging/deleting it. This wraps a <see cref="GitSimEngine"/> and
    /// corrects ONLY the <c>checkout HEAD -- &lt;path&gt;</c> shape
    /// <see cref="WorktreeFilter"/> emits (by first asking GitSim's own, correctly
    /// modeled <c>cat-file -e HEAD:&lt;path&gt;</c> whether the path exists), delegating
    /// every other call unchanged. Never edits GitSim itself.
    /// </summary>
    private sealed class HeadCheckoutAwareGitInvoker(GitSimEngine sim) : IGitInvoker
    {
        public async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath, IEnumerable<string> arguments, CancellationToken ct,
            TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default, Action<string>? onActivity = null)
        {
            var argv = arguments as string[] ?? arguments.ToArray();
            if (argv is ["checkout", "HEAD", "--", var rel])
            {
                var probe = await sim.RunAsync(rootPath, ["cat-file", "-e", $"HEAD:{rel}"], ct);
                if (probe.ExitCode != 0)
                    return (1, $"error: pathspec '{rel}' did not match any file(s) known to git\n", false);
            }

            return await sim.RunAsync(rootPath, argv, ct, timeout, environment, killToken, onActivity);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Non-test dirty tracked files are reverted to HEAD
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_RevertsNonTestTrackedModifications()
    {
        using var repo = TestRepository.Create();
        var prodFile = await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");
        var testFile = Path.Combine(repo.Root, "tests", "app.tests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        await File.WriteAllTextAsync(testFile, "// test");

        // Modify the production file.
        await File.WriteAllTextAsync(prodFile, "modified by agent");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["tests/app.tests.cs"], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        // Production file reverted.
        Assert.Equal("original", await File.ReadAllTextAsync(prodFile));
        // Test file untouched.
        Assert.Equal("// test", await File.ReadAllTextAsync(testFile));
    }

    // ═══════════════════════════════════════════════════════════════
    // Non-test untracked files are deleted
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_DeletesNonTestUntrackedFiles()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // Agent created an untracked production file.
        var untrackedProd = Path.Combine(repo.Root, "src", "helper.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(untrackedProd)!);
        await File.WriteAllTextAsync(untrackedProd, "helper");

        // Agent created an untracked test file (should survive).
        var testFile = Path.Combine(repo.Root, "tests", "app.tests.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(testFile)!);
        await File.WriteAllTextAsync(testFile, "// test");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["tests/app.tests.cs"], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        // Non-test untracked deleted.
        Assert.False(File.Exists(untrackedProd), "non-test untracked file should be deleted");
        // Test file preserved.
        Assert.True(File.Exists(testFile), "test file should survive");
    }

    // ═══════════════════════════════════════════════════════════════
    // TestFiles paths are left untouched (tracked or untracked)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_LeavesTestFilesUntouched()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // Create a tracked test file that is dirty.
        var trackedTest = Path.Combine(repo.Root, "tests", "app.tests.cs");
        var sim = new GitSimEngine();
        sim.Seed(repo.Root, "tests/app.tests.cs", "old test");
        sim.Commit(repo.Root, "add test file");
        // Now modify it (simulating agent authoring a test).
        await File.WriteAllTextAsync(trackedTest, "// updated test");

        // Also create an untracked production stub (should be deleted).
        var stub = Path.Combine(repo.Root, "src", "stub.cs");
        await File.WriteAllTextAsync(stub, "stub");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["tests/app.tests.cs"], tasksDir: null, sim, CancellationToken.None);

        // Tracked test file is NOT reverted.
        Assert.Equal("// updated test", await File.ReadAllTextAsync(trackedTest));
        // Non-test stub is deleted.
        Assert.False(File.Exists(stub), "non-test stub should be deleted");
    }

    // ═══════════════════════════════════════════════════════════════
    // Internal artifacts (.relay/) are preserved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_PreservesInternalArtifacts()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // An untracked file under .relay/ (internal artifact).
        var artifactPath = Path.Combine(repo.Root, ".relay", "task-123", "ledger.md");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        await File.WriteAllTextAsync(artifactPath, "# Ledger");

        // Also a .relay-scratch/ file.
        var scratchPath = Path.Combine(repo.Root, ".relay-scratch", "temp.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(scratchPath)!);
        await File.WriteAllTextAsync(scratchPath, "scratch");

        // And a .swival/ file.
        var swivalPath = Path.Combine(repo.Root, ".swival", "cache");
        Directory.CreateDirectory(Path.GetDirectoryName(swivalPath)!);
        await File.WriteAllTextAsync(swivalPath, "cache");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        Assert.True(File.Exists(artifactPath), ".relay/ artifact should be preserved");
        Assert.True(File.Exists(scratchPath), ".relay-scratch/ artifact should be preserved");
        Assert.True(File.Exists(swivalPath), ".swival/ artifact should be preserved");
    }

    // ═══════════════════════════════════════════════════════════════
    // Tasks-dir files are preserved
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_PreservesTasksDirFiles()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        var tasksDir = "llm-tasks";
        var taskFilePath = Path.Combine(repo.Root, tasksDir, "task-001.md");
        Directory.CreateDirectory(Path.GetDirectoryName(taskFilePath)!);
        await File.WriteAllTextAsync(taskFilePath, "# Task");

        await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: tasksDir, new GitSimEngine(), CancellationToken.None);

        Assert.True(File.Exists(taskFilePath), "tasks-dir file should be preserved");
    }

    // ═══════════════════════════════════════════════════════════════
    // Clean tree — idempotent no-op
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task DiscardNonTestEditsAsync_CleanTree_IsNoOp()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");
        // Tree is clean.

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, new GitSimEngine(), CancellationToken.None);

        Assert.Empty(result.TrackedDiscarded);
        Assert.Empty(result.UntrackedDeleted);
    }

}
