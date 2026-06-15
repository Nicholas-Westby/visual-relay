using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for how <see cref="WorktreeFilter.DiscardNonTestEditsAsync"/> handles
/// staged COPY (<c>C</c>) records under <c>git diff --cached --name-status
/// -M -C -z</c>.  A copy has the same three-token <c>-z</c> shape as a rename
/// (<c>C100\0old\0new\0</c>) but DIFFERENT semantics: it does NOT delete its
/// source.  So a copy's destination is a plain staged addition that must be
/// reverted/deleted — never rename-protected — whereas a true rename's
/// destination is protected because it may hold the only surviving copy of the
/// (deleted) source.  Companion to WorktreeFilterTests.ResidualLeaks.cs;
/// inherits the GitInvoker collection membership of the main partial.
/// </summary>
public sealed partial class WorktreeFilterTests
{
    /// <summary>
    /// Leak 3: a staged copy emits a <c>C</c> name-status record. The parser
    /// must capture the copy DESTINATION so the new (non-test) production file
    /// does not leak into stage 6. <c>-M</c> alone suppresses copy detection,
    /// so a real <c>C</c> record only reaches the parser once the command also
    /// passes <c>-C</c>; we inject the NUL-delimited <c>C</c> record via
    /// <see cref="GitInvoker.Override"/> (the exact <c>-z</c> shape the command
    /// requests) and delegate every other git call to the real repo. The copy
    /// destination is a new staged file absent from HEAD, so the filter
    /// unstages and deletes it. (Non-test source → already worked pre-fix.)
    /// </summary>
    [Fact]
    public async Task CopyRecordDestination_IsRevertedOrDeleted()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "source.cs", "source-content");

        // The copy destination: a new (non-test) production file staged into
        // the index — a real staged addition the filter can unstage + delete.
        var copyPath = Path.Combine(repo.Root, "copy.cs");
        await File.WriteAllTextAsync(copyPath, "source-content");
        TestGit.Run(repo.Root, "add", "copy.cs");

        GitInvoker.ResetForTests();
        var myRoot = repo.Root;
        var envRemove = new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
        GitInvoker.Override = (binary, args, rootPath, ct, timeout, env) =>
        {
            var argv = args as string[] ?? args.ToArray();
            // Intercept ONLY the staged name-status enumeration and return the
            // -z C record git emits for a copy (status\0old\0new\0).
            if (rootPath == myRoot
                && argv.Contains("--name-status")
                && argv.Contains("--cached"))
            {
                return Task.FromResult((0, "C100\0source.cs\0copy.cs\0", false));
            }

            return ProcessCapture.RunAsync(
                binary, ["-C", rootPath, .. argv], rootPath,
                timeout ?? TimeSpan.FromSeconds(30), ct, env, envRemove: envRemove);
        };

        try
        {
            var result = await WorktreeFilter.DiscardNonTestEditsAsync(
                repo.Root, [], tasksDir: null, CancellationToken.None);

            Assert.Null(result.Error);
            // CRITICAL: the copy destination (a new production file) must NOT
            // survive into stage 6.
            Assert.False(File.Exists(copyPath),
                "copy destination copy.cs must be unstaged and removed");
            Assert.Contains("copy.cs", result.TrackedDiscarded, StringComparer.Ordinal);
        }
        finally
        {
            GitInvoker.ResetForTests();
        }
    }

    /// <summary>
    /// Regression (review follow-up B-1): a COPY whose SOURCE is a declared
    /// testFile and whose DESTINATION is a production path must NOT be
    /// rename-protected. A rename (<c>R</c>) DELETES its source, so its
    /// destination may hold the only surviving copy → excluding it is correct.
    /// A copy (<c>C</c>) does NOT delete its source, so the copy's PRODUCTION
    /// destination is just a staged addition that must be reverted/deleted.
    /// Under 620d297 the copy is fed into <c>renamePairs</c> like a rename and
    /// <see cref="WorktreeFilter.ComputeRenameExclusions"/> excludes the
    /// destination (the source is a testFile) → <c>prod2.cs</c> leaks into
    /// stage 6 (RED). After the fix it is unstaged + deleted and the test
    /// source is untouched. The NUL-delimited <c>C</c> record is injected via
    /// <see cref="GitInvoker.Override"/> (the <c>-z</c> shape git emits for
    /// <c>cp my.Tests.cs prod2.cs</c> under <c>-M -C</c>); other git calls hit
    /// the real repo.
    /// </summary>
    [Fact]
    public async Task CopyFromTestFileToProd_ProdDestinationIsRevertedOrDeleted_SourceUntouched()
    {
        using var repo = TestRepository.Create();
        // Source = a declared testFile committed to HEAD (assert it survives).
        var testSourcePath = await InitRepoWithTrackedFile(
            repo.Root, "my.Tests.cs", "test-content");

        // Destination = a NEW prod file staged into the index — a real staged
        // addition the filter must unstage + delete (a copy does not delete
        // its source, so this prod path is NOT rename-protected).
        var prodDestPath = Path.Combine(repo.Root, "prod2.cs");
        await File.WriteAllTextAsync(prodDestPath, "test-content");
        TestGit.Run(repo.Root, "add", "prod2.cs");

        GitInvoker.ResetForTests();
        var myRoot = repo.Root;
        var envRemove = new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
        GitInvoker.Override = (binary, args, rootPath, ct, timeout, env) =>
        {
            var argv = args as string[] ?? args.ToArray();
            // Intercept ONLY the staged name-status enumeration; return the
            // -z C record git emits for `cp my.Tests.cs prod2.cs` (-M -C).
            if (rootPath == myRoot
                && argv.Contains("--name-status")
                && argv.Contains("--cached"))
            {
                return Task.FromResult((0, "C100\0my.Tests.cs\0prod2.cs\0", false));
            }

            return ProcessCapture.RunAsync(
                binary, ["-C", rootPath, .. argv], rootPath,
                timeout ?? TimeSpan.FromSeconds(30), ct, env, envRemove: envRemove);
        };

        try
        {
            var result = await WorktreeFilter.DiscardNonTestEditsAsync(
                repo.Root, ["my.Tests.cs"], tasksDir: null, CancellationToken.None);

            Assert.Null(result.Error);

            // CRITICAL: the prod copy destination must NOT survive into stage 6.
            Assert.False(File.Exists(prodDestPath),
                "copy destination prod2.cs (a production addition) must be unstaged and removed, not rename-protected");
            Assert.Contains("prod2.cs", result.TrackedDiscarded, StringComparer.Ordinal);

            // The test SOURCE must be untouched — the copy left it in place.
            Assert.True(File.Exists(testSourcePath),
                "the copy source my.Tests.cs (a testFile) must be left untouched");
            Assert.Equal("test-content", await File.ReadAllTextAsync(testSourcePath));
            Assert.DoesNotContain("my.Tests.cs", result.TrackedDiscarded, StringComparer.Ordinal);
        }
        finally
        {
            GitInvoker.ResetForTests();
        }
    }
}
