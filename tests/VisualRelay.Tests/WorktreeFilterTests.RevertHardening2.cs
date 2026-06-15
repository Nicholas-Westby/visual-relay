using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Red-first tests for data-loss defects C–F in
/// <see cref="WorktreeFilter.DiscardNonTestEditsAsync"/> revert logic:
/// quotePath, case-comparer, rm --cached, and File.Delete error routing.
/// </summary>
public sealed partial class WorktreeFilterTests
{
    // ═══════════════════════════════════════════════════════════════
    // Defect C: non-ASCII / spaced paths with quotePath
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Files with non-ASCII characters or spaces in their names must
    /// be handled correctly — tracked reverted, untracked deleted,
    /// and a non-ASCII testFile preserved.  The filter must disable
    /// <c>core.quotePath</c> so git emits the real path.
    /// </summary>
    [Fact]
    public async Task NonAsciiAndSpacedPaths_HandledCorrectly()
    {
        using var repo = TestRepository.Create();

        // A tracked, non-ASCII test file — must be preserved.
        var testFileRel = "café file.txt";
        var testFilePath = await InitRepoWithTrackedFile(repo.Root, testFileRel, "test-content");
        await File.WriteAllTextAsync(testFilePath, "// updated test");

        // A tracked production file with non-ASCII name — must be reverted.
        var prodTrackedRel = "src/über file.txt";
        var prodTrackedPath = await InitRepoWithTrackedFile(repo.Root, prodTrackedRel, "prod-original");
        await File.WriteAllTextAsync(prodTrackedPath, "prod-modified");

        // An untracked non-ASCII junk file — must be deleted.
        var junkRel = "junk café.txt";
        var junkPath = Path.Combine(repo.Root, junkRel);
        Directory.CreateDirectory(Path.GetDirectoryName(junkPath)!);
        await File.WriteAllTextAsync(junkPath, "junk");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [testFileRel], tasksDir: null, CancellationToken.None);

        Assert.Null(result.Error);

        // Non-ASCII test file preserved.
        Assert.True(File.Exists(testFilePath),
            "non-ASCII test file must be preserved");
        Assert.Equal("// updated test", await File.ReadAllTextAsync(testFilePath));

        // Non-ASCII tracked production file reverted.
        Assert.True(File.Exists(prodTrackedPath),
            "non-ASCII tracked production file must still exist (reverted)");
        Assert.Equal("prod-original", await File.ReadAllTextAsync(prodTrackedPath));

        // Non-ASCII untracked junk deleted.
        Assert.False(File.Exists(junkPath),
            "non-ASCII untracked junk must be deleted");
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect D: case-sensitive vs insensitive comparer
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// On a case-sensitive host, a production path that differs from
    /// a testFile only by case must be reverted — the testSet must
    /// not case-fold it into a test-file match.  The test adapts its
    /// assertion to the host filesystem so it is deterministic in CI.
    /// </summary>
    [Fact]
    public async Task CaseSensitiveHost_ProductionPathDifferingByCase_Reverted()
    {
        using var repo = TestRepository.Create();

        // Production file: tests/Helper.cs (uppercase H) — committed,
        // then modified.
        var prodRel = "tests/Helper.cs";
        var prodPath = await InitRepoWithTrackedFile(repo.Root, prodRel, "prod-original");
        await File.WriteAllTextAsync(prodPath, "prod-modified");

        // Declare testFile with different case: tests/helper.cs (lowercase h).
        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["tests/helper.cs"], tasksDir: null, CancellationToken.None);

        var isCaseInsensitiveHost = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

        if (isCaseInsensitiveHost)
        {
            Assert.True(File.Exists(prodPath),
                "on case-insensitive host, production path is the test file — must be preserved");
            Assert.Equal("prod-modified", await File.ReadAllTextAsync(prodPath));
        }
        else
        {
            Assert.True(File.Exists(prodPath),
                "production file must still exist on disk (reverted, not deleted)");
            Assert.Equal("prod-original", await File.ReadAllTextAsync(prodPath));
        }

        Assert.Null(result.Error);
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect E: git rm --cached on absent path → spurious flag
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When <c>git rm --cached</c> is called on a path that is not in
    /// the index (exit 128), the filter must treat this as benign and
    /// NOT surface a spurious error flag.
    /// </summary>
    [Fact]
    public async Task GitRmCachedOnAbsentPath_NoSpuriousFlag()
    {
        using var repo = TestRepository.Create();

        // Commit a baseline first so the repo is valid.
        var prodPath = await InitRepoWithTrackedFile(repo.Root, "src/app.cs", "original");

        // Create a new tracked file AFTER the commit — it will be staged
        // but never committed, so it's absent from HEAD.
        var newFilePath = Path.Combine(repo.Root, "src", "new-file.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(newFilePath)!);
        await File.WriteAllTextAsync(newFilePath, "new");
        TestGit.Run(repo.Root, "add", "src/new-file.cs");

        // We use an override to make rm --cached return 128 to
        // simulate the edge case where the path somehow isn't in
        // the index.  Before the fix this produces a spurious flag.
        GitInvoker.ResetForTests();
        var myRoot = repo.Root;
        var envRemove = new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
        var rmCalled = false;
        GitInvoker.Override = (binary, args, rootPath, ct, timeout, env) =>
        {
            if (rootPath != myRoot)
            {
                return ProcessCapture.RunAsync(
                    binary, ["-C", rootPath, .. args], rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), ct, env, envRemove: envRemove);
            }

            if (args.Any(a => a == "rm"))
            {
                rmCalled = true;
                return Task.FromResult((128, "fatal: pathspec did not match any files", false));
            }

            return ProcessCapture.RunAsync(
                binary, ["-C", rootPath, .. args], rootPath,
                timeout ?? TimeSpan.FromSeconds(30), ct, env, envRemove: envRemove);
        };

        try
        {
            var result = await WorktreeFilter.DiscardNonTestEditsAsync(
                repo.Root, [], tasksDir: null, CancellationToken.None);

            Assert.True(rmCalled, "rm --cached should have been called");
            Assert.Null(result.Error);
        }
        finally
        {
            GitInvoker.ResetForTests();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Defect F: File.Delete throws → folded into Error, no half-mutation
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Error accumulation during the tracked-revert phase must NOT prevent
    /// the untracked-delete phase from running (no half-mutation).  Errors
    /// from both phases must be folded into <c>WorktreeFilterResult.Error</c>,
    /// and <c>TrackedDiscarded</c> must list only paths actually processed.
    /// Uses <see cref="GitInvoker.Override"/> (checkout timeout + absent-from-HEAD
    /// probe) — no OS-specific filesystem manipulation.
    /// </summary>
    [Fact]
    public async Task FileDeleteThrows_FoldedIntoError_NoHalfMutation()
    {
        using var repo = TestRepository.Create();

        // ── Set up two tracked files committed to HEAD ─────────────
        // Both are created and committed in a single setup so there is
        // no ambiguity about what git sees.
        var dir = Path.Combine(repo.Root, "src");
        Directory.CreateDirectory(dir);
        var prodPathA = Path.Combine(repo.Root, "src", "app.cs");
        var prodPathB = Path.Combine(repo.Root, "src", "lib.cs");
        await File.WriteAllTextAsync(prodPathA, "original-A");
        await File.WriteAllTextAsync(prodPathB, "original-B");

        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        // Modify both so they appear dirty.
        await File.WriteAllTextAsync(prodPathA, "modified-A");
        await File.WriteAllTextAsync(prodPathB, "modified-B");

        // An untracked non-test file — proves phase 4 ran.
        var untrackedPath = Path.Combine(repo.Root, "src", "junk.cs");
        await File.WriteAllTextAsync(untrackedPath, "junk");

        GitInvoker.ResetForTests();
        var myRoot = repo.Root;
        var envRemove = new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
        GitInvoker.Override = (binary, args, rootPath, ct, timeout, env) =>
        {
            if (rootPath != myRoot)
            {
                return ProcessCapture.RunAsync(
                    binary, ["-C", rootPath, .. args], rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), ct, env, envRemove: envRemove);
            }

            if (args.Any(a => a == "checkout"))
            {
                var path = args.Last();
                if (path == "src/app.cs")
                    return Task.FromResult((0, "", true)); // timeout
                if (path == "src/lib.cs")
                    return Task.FromResult((1, "simulated failure", false));
                return ProcessCapture.RunAsync(
                    binary, ["-C", rootPath, .. args], rootPath,
                    timeout ?? TimeSpan.FromSeconds(30), ct, env, envRemove: envRemove);
            }

            // Make src/lib.cs appear absent from HEAD for the rm+delete path.
            if (args.Any(a => a == "cat-file") && args.Any(a => a.Contains("src/lib.cs")))
                return Task.FromResult((1, "", false));

            // All other commands run normally.
            return ProcessCapture.RunAsync(
                binary, ["-C", rootPath, .. args], rootPath,
                timeout ?? TimeSpan.FromSeconds(30), ct, env, envRemove: envRemove);
        };

        try
        {
            var result = await WorktreeFilter.DiscardNonTestEditsAsync(
                repo.Root, [], tasksDir: null, CancellationToken.None);

            // Error must be non-null — checkout timeout produces an error.
            Assert.NotNull(result.Error);
            Assert.Contains("src/app.cs", result.Error, StringComparison.Ordinal);

            // No half-mutation: phase 4 (untracked delete) completed.
            Assert.False(File.Exists(untrackedPath),
                "untracked file must be deleted — phase 4 completed despite tracked-phase errors");

            // Tracked file A survived (checkout timed out → not deleted).
            Assert.True(File.Exists(prodPathA), "tracked file A must survive timed-out checkout");
            Assert.Equal("modified-A", await File.ReadAllTextAsync(prodPathA));

            // Tracked file B was deleted (absent-from-HEAD path).
            Assert.False(File.Exists(prodPathB), "absent-from-HEAD tracked file B must be deleted");

            // TrackedDiscarded lists only paths actually processed.
            Assert.DoesNotContain("src/app.cs", result.TrackedDiscarded, StringComparer.Ordinal);
            Assert.Contains("src/lib.cs", result.TrackedDiscarded, StringComparer.Ordinal);
            Assert.Contains("src/junk.cs", result.UntrackedDeleted, StringComparer.Ordinal);
        }
        finally
        {
            GitInvoker.ResetForTests();
        }
    }
}
