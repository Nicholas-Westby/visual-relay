using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

public sealed partial class VerifyWorktreeIgnoredOverlayCopyTests
{
    // ───────────────────────────────────────────────────────────────────
    // 9. Dangling link: a symlink whose target does not exist is recreated
    //    verbatim without aborting the copy — a sibling real file must still
    //    be copied.
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_DanglingSymlink_IsRecreatedWithoutAbortingCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-dangle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var driver = NewDriver();
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "deps/\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            // deps/ghost -> ./missing (dangling target — target file never created).
            Directory.CreateDirectory(Path.Combine(root, "deps"));
            File.CreateSymbolicLink(Path.Combine(root, "deps", "ghost"), "./missing");
            // Sibling real file must still be copied.
            await File.WriteAllTextAsync(Path.Combine(root, "deps", "real.txt"), "real");
            await CommitAll(root, "seed");

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-dangle", "run-dangle", CancellationToken.None, LowThresholdBytes);

            // Dangling symlink must appear in a directory listing (File.Exists
            // follows symlinks on .NET and returns false when the target is missing,
            // but the link node itself must be present).
            var worktreeDeps = Path.Combine(worktree, "deps");
            var depsEntries = Directory.EnumerateFileSystemEntries(worktreeDeps)
                .Select(Path.GetFileName).ToList();
            Assert.Contains("ghost", depsEntries);
            Assert.Contains("real.txt", depsEntries);

            // Sibling real file was copied.
            var realFile = Path.Combine(worktreeDeps, "real.txt");
            Assert.True(File.Exists(realFile));
            Assert.Equal("real", await File.ReadAllTextAsync(realFile));
        }
        finally
        {
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // 10. Warn event on overlay failure: when an ignored entry can't be
    //     overlaid (e.g. an unreadable file), a verify_overlay_skipped warn
    //     event is published and worktree creation still completes (the
    //     never-abort contract is unchanged).
    // ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateVerifyWorktree_UnreadableIgnoredFile_PublishesWarnEventAndCompletes()
    {
        var root = Path.Combine(Path.GetTempPath(), "vr-vw-warn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sink = new InMemoryRelayEventSink();
        var driver = new RelayDriver(RelayDriverDependencies.ForTests(
            new ScriptedSubagentRunner(), new ScriptedTestRunner(), sink, new GitSimEngine()));
        string? worktree = null;
        try
        {
            InitRepo(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "secret.txt\n");
            await File.WriteAllTextAsync(Path.Combine(root, "tracked.txt"), "tracked");
            var secretFile = Path.Combine(root, "secret.txt");
            await File.WriteAllTextAsync(secretFile, "SHOULD-NOT-BE-READABLE");
            await CommitAll(root, "seed");

            // Make the ignored file unreadable so File.Copy throws —
            // this triggers the overlay catch and fires the warn event.
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(secretFile, UnixFileMode.None);

            worktree = await driver.CreateVerifyWorktreeForTestAsync(
                root, "task-warn", "run-warn", CancellationToken.None, LowThresholdBytes);

            // Worktree must still exist — the overlay catch never aborts.
            Assert.True(Directory.Exists(worktree),
                "worktree must be created even when an overlay entry fails");

            // A verify_overlay_skipped warn event must have been published.
            var warnEvent = sink.Events.FirstOrDefault(e =>
                e.Level == "warn" && e.EventName == "verify_overlay_skipped");
            Assert.NotNull(warnEvent);
            Assert.NotNull(warnEvent!.Data);
            Assert.True(warnEvent.Data!.ContainsKey("entry"),
                "warn event data must name the failed entry");
            Assert.True(warnEvent.Data!.ContainsKey("error"),
                "warn event data must include the exception message");
        }
        finally
        {
            // Restore permissions so the resilient delete can clean up.
            try
            {
                var secretFile = Path.Combine(root, "secret.txt");
                if (File.Exists(secretFile) && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                    File.SetUnixFileMode(secretFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* best-effort */ }
            if (worktree is not null)
                await driver.CleanupVerifyWorktreeForTestAsync(root, worktree, CancellationToken.None);
            TestFileSystem.DeleteDirectoryResilient(root);
        }
    }
}
