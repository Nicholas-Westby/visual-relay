using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Red-first tests for the four residual correctness leaks in
/// <see cref="WorktreeFilter.DiscardNonTestEditsAsync"/> — each lets a
/// non-test edit survive into stage 6:
/// <list type="number">
/// <item>TAB/newline in a path is C-quoted by git and never dequoted →
///   the real file is missed (leak).</item>
/// <item>Trailing/leading whitespace in a filename is mangled by the
///   <c>line.Trim()</c> in enumeration parsing → the real file is missed.</item>
/// <item>A COPY (<c>C</c>) record's destination is dropped because the
///   parser keys rename detection on <c>R</c> only.</item>
/// <item>A prod→test rename leaves the staged deletion of the old
///   production path surviving into stage 6 (over-exclude).</item>
/// </list>
/// Fixes 1 and 2 are addressed together by switching all enumerations to
/// NUL-delimited (<c>-z</c>) parsing.
/// </summary>
public sealed partial class WorktreeFilterTests
{
    // ═══════════════════════════════════════════════════════════════
    // Leak 1: TAB in a non-test path is reverted (not missed)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A tracked production file whose name contains a TAB is C-quoted by
    /// git in non-<c>-z</c> output (<c>"tab\tfile.txt"</c>) regardless of
    /// <c>core.quotePath</c>.  The quoted literal never matches the real
    /// on-disk path, so <c>git checkout</c> misses it and the edit leaks.
    /// With <c>-z</c> the path is emitted verbatim and the edit is reverted.
    /// </summary>
    [Fact]
    public async Task TabInTrackedPath_NonTestEditIsReverted()
    {
        using var repo = TestRepository.Create();
        var relPath = "src/tab\tfile.cs";        // literal TAB in the name
        var full = await InitRepoWithTrackedFile(repo.Root, relPath, "original");

        // Agent modifies the production file.
        await File.WriteAllTextAsync(full, "modified-by-agent");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

        Assert.Null(result.Error);
        // ── CRITICAL: the TAB-named production edit must be reverted ──
        Assert.Equal("original", await File.ReadAllTextAsync(full));
        Assert.Contains(relPath, result.TrackedDiscarded, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Leak 1 (newline variant): NEWLINE in a non-test path is reverted
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A tracked production file whose name contains a literal NEWLINE is
    /// C-quoted in non-<c>-z</c> output and would also be split by the
    /// line-oriented capture reader.  With <c>-z</c> the embedded newline is
    /// preserved verbatim before its NUL terminator (the capture reader strips
    /// it on read but <c>AppendLine</c> re-inserts it), so the path round-trips
    /// and the edit is reverted — and no spurious empty path leaks into the
    /// result lists.
    /// </summary>
    [Fact]
    public async Task NewlineInTrackedPath_NonTestEditIsReverted()
    {
        using var repo = TestRepository.Create();
        var relPath = "src/line\nbreak.cs";       // literal newline in the name
        var full = await InitRepoWithTrackedFile(repo.Root, relPath, "original");

        await File.WriteAllTextAsync(full, "modified-by-agent");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

        Assert.Null(result.Error);
        // ── CRITICAL: the newline-named production edit must be reverted ──
        Assert.Equal("original", await File.ReadAllTextAsync(full));
        Assert.Contains(relPath, result.TrackedDiscarded, StringComparer.Ordinal);
        // No phantom newline-only entry leaks into the result list.
        Assert.DoesNotContain("\n", result.TrackedDiscarded, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Leak 2: trailing-whitespace non-test path is reverted (not mangled)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// A tracked production file whose name ends in a space survives the
    /// enumeration only if the parser does NOT <c>Trim()</c> the git-emitted
    /// path.  The old <c>line.Trim()</c> stripped the trailing space, so the
    /// mangled path missed the real file and the edit leaked.
    /// </summary>
    [Fact]
    public async Task TrailingSpaceInTrackedPath_NonTestEditIsReverted()
    {
        using var repo = TestRepository.Create();
        // A true trailing space at the very end of the filename — the
        // old line.Trim() in the parser stripped this, mangling the path.
        var relPath = "src/trailingspace.cs ";
        var full = await InitRepoWithTrackedFile(repo.Root, relPath, "original");

        await File.WriteAllTextAsync(full, "modified-by-agent");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, [], tasksDir: null, CancellationToken.None);

        Assert.Null(result.Error);
        // ── CRITICAL: the trailing-space production edit must be reverted ──
        Assert.Equal("original", await File.ReadAllTextAsync(full));
        Assert.Contains(relPath, result.TrackedDiscarded, StringComparer.Ordinal);
    }

    // ═══════════════════════════════════════════════════════════════
    // Leak 3: COPY record's destination is reverted/deleted
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When the target repo enables copy detection, a staged copy emits a
    /// <c>C</c> name-status record (<c>C100\0old\0new\0</c>) — the same
    /// three-part shape as a rename but with a <c>C</c> status.  The parser
    /// must capture the copy DESTINATION so the new (non-test) production
    /// file does not leak into stage 6.
    /// <para>
    /// The hardcoded <c>-M</c> on the staged-diff command suppresses copy
    /// detection, so a real <c>C</c> record only reaches the parser once the
    /// command also passes <c>-C</c>.  To exercise the parser directly we
    /// inject a NUL-delimited <c>C</c> record via <see cref="GitInvoker.Override"/>
    /// (the exact <c>-z</c> shape the fixed command requests) and delegate
    /// every other git call to the real repo.  The copy destination is a new
    /// staged file absent from HEAD, so the filter unstages and deletes it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CopyRecordDestination_IsRevertedOrDeleted()
    {
        using var repo = TestRepository.Create();
        await InitRepoWithTrackedFile(repo.Root, "source.cs", "source-content");

        // The copy destination: a new (non-test) production file staged into
        // the index.  Create it on disk and stage it so it is a real staged
        // addition that the filter can unstage + delete.
        var copyPath = Path.Combine(repo.Root, "copy.cs");
        await File.WriteAllTextAsync(copyPath, "source-content");
        TestGit.Run(repo.Root, "add", "copy.cs");

        GitInvoker.ResetForTests();
        var myRoot = repo.Root;
        var envRemove = new HashSet<string>(StringComparer.Ordinal) { "DEVELOPER_DIR", "SDKROOT" };
        GitInvoker.Override = (binary, args, rootPath, ct, timeout, env) =>
        {
            // Intercept ONLY the staged name-status enumeration for this repo
            // and return a NUL-delimited C record (the -z shape git emits for
            // a copy: status\0old\0new\0).  This is what the fixed command
            // requests; current code requests non-z output and mis-parses it,
            // dropping copy.cs entirely → leak (red).
            if (rootPath == myRoot
                && args.Contains("--name-status")
                && args.Contains("--cached"))
            {
                return Task.FromResult((0, "C100\0source.cs\0copy.cs\0", false));
            }

            return ProcessCapture.RunAsync(
                binary, ["-C", rootPath, .. args], rootPath,
                timeout ?? TimeSpan.FromSeconds(30), ct, env, envRemove: envRemove);
        };

        try
        {
            var result = await WorktreeFilter.DiscardNonTestEditsAsync(
                repo.Root, [], tasksDir: null, CancellationToken.None);

            Assert.Null(result.Error);
            // ── CRITICAL: the copy destination (a new production file) must
            // NOT survive into stage 6 ──
            Assert.False(File.Exists(copyPath),
                "copy destination copy.cs must be unstaged and removed");
            Assert.Contains("copy.cs", result.TrackedDiscarded, StringComparer.Ordinal);
        }
        finally
        {
            GitInvoker.ResetForTests();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Leak 4: prod→test rename leaves no stray staged deletion
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// When a staged rename's DESTINATION is a testFile but its SOURCE is a
    /// production file, excluding BOTH endpoints leaves the staged DELETION
    /// of the old production path surviving into stage 6 (a production
    /// change leaked).  The conservative fix restores the production source
    /// from HEAD (reverting its staged deletion) while leaving the test
    /// destination intact — so stage 6 sees the production tree unchanged
    /// and the authored test content preserved.
    /// </summary>
    [Fact]
    public async Task ProdToTestRename_RestoresProdSource_NoStrayStagedDeletion()
    {
        using var repo = TestRepository.Create();

        // prod.cs is a production file committed to HEAD.
        var prodPath = await InitRepoWithTrackedFile(repo.Root, "prod.cs", "prod-content");

        // Stage rename: prod.cs → some.Tests.cs (destination is the testFile).
        TestGit.Run(repo.Root, "mv", "prod.cs", "some.Tests.cs");

        var result = await WorktreeFilter.DiscardNonTestEditsAsync(
            repo.Root, ["some.Tests.cs"], tasksDir: null, CancellationToken.None);

        Assert.Null(result.Error);

        // ── CRITICAL: no stray staged deletion of the production source ──
        // The index must NOT stage prod.cs as deleted heading into stage 6.
        var stagedStatus = TestGit.Run(repo.Root, "diff", "--cached", "--name-status", "-M");
        Assert.DoesNotContain("prod.cs", stagedStatus, StringComparison.Ordinal);

        // Production source restored on disk from HEAD.
        Assert.True(File.Exists(prodPath),
            "production source prod.cs must be restored (its staged deletion reverted)");
        Assert.Equal("prod-content", await File.ReadAllTextAsync(prodPath));

        // The test destination must survive — it holds the authored content.
        var destPath = Path.Combine(repo.Root, "some.Tests.cs");
        Assert.True(File.Exists(destPath),
            "test destination some.Tests.cs must survive");
        Assert.Equal("prod-content", await File.ReadAllTextAsync(destPath));
    }
}
