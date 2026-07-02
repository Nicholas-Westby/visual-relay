using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Resilience tests: TOCTOU race between git ls-files --others and git add,
// plus Unicode path handling (U+202F NARROW NO-BREAK SPACE).
public sealed partial class GitCommitterAutoIncludeTests
{
    // U+202F NARROW NO-BREAK SPACE — legitimate in macOS filenames, emitted by
    // the app's built-in ControlScreenshot feature.  Must survive the full
    // auto-include pipeline without triggering spurious failures.
    private const string NarrowNoBreakSpace = "\u202F";

    // ── TOCTOU resilience ────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_SkipsVanishedFile_BetweenSnapshotAndAdd()
    {
        // When a file is listed by git ls-files --others but disappears
        // before git add (TOCTOU race), the auto-include pass must NOT fail
        // the whole commit.  The existence gate (File.Exists/Directory.Exists)
        // must skip the vanished file and commit only the extant ones.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Create a real authored file AND a sibling whose path we will
        // inject into the snapshot as "stale" (it never exists on disk).
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "tests-extra.cs"), "// real");

        var staleRelPath = "ghost-file.cs";
        var staleInvoker = new StaleSnapshotGitInvoker(staleRelPath);

        var manifest = new[] { "src/app.cs" };

        var result = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked,
            tasksDir: null,
            cancellationToken: CancellationToken.None,
            gitInvoker: staleInvoker);

        Assert.True(result.Success, result.Error);
        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        // The real authored file IS committed.
        Assert.Contains("tests-extra.cs", committed);
        // The ghost path was never on disk and must NOT appear.
        Assert.DoesNotContain(staleRelPath, committed);
    }

    // ── Unicode path handling ─────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_AutoIncludesFileWithUnicodeNarrowNoBreakSpace()
    {
        // A newly authored file whose name contains U+202F NARROW NO-BREAK
        // SPACE must be successfully auto-included end-to-end.  This character
        // is emitted by the app's ControlScreenshot feature and must survive
        // the full git ls-files → filter → git add pipeline.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Disable git's core.quotePath so non-ASCII paths are emitted
        // verbatim rather than C-quoted (e.g. \342\200\257 for U+202F).
        TestGit.Run(repo.Root, "config", "core.quotePath", "false");

        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");

        // File name with U+202F between "05" and "AM" — mirrors the real
        // screenshot filename that triggered the original failure.
        var unicodeName = $"Screenshot 2026-07-01 at 9.59.05{NarrowNoBreakSpace}AM.png";
        var unicodePath = Path.Combine(repo.Root, unicodeName);
        File.WriteAllText(unicodePath, "fake screenshot");

        var manifest = new[] { "src/app.cs" };

        var result = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked,
            tasksDir: null,
            CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        Assert.Contains(unicodeName, committed);
    }

    [Fact]
    public async Task CommitAsync_ExcludesTasksDirFileWithUnicodeInPath()
    {
        // A file dropped under the tasks dir whose path contains U+202F
        // must be excluded from auto-include.  The tasks-dir guard must work
        // regardless of Unicode characters in the path.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "old");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Disable git's core.quotePath so non-ASCII paths are emitted verbatim.
        TestGit.Run(repo.Root, "config", "core.quotePath", "false");

        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "updated");
        File.WriteAllText(Path.Combine(repo.Root, "src", "new-impl.cs"), "// genuinely authored");

        // Tasks-dir file with U+202F in the directory name.
        var tasksDirName = $"task{NarrowNoBreakSpace}dir";
        var tasksDirFull = Path.Combine(repo.Root, "llm-tasks", tasksDirName);
        Directory.CreateDirectory(tasksDirFull);
        File.WriteAllText(Path.Combine(tasksDirFull, "task.md"), "# user task");

        var manifest = new[] { "src/app.cs" };

        var commit = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked,
            tasksDir: "llm-tasks",
            CancellationToken.None);
        Assert.True(commit.Success, commit.Error);

        var committed = TestGit.Run(repo.Root, "show", "--name-only", "--pretty=format:", "HEAD");
        // Genuinely authored file outside tasks dir IS auto-included.
        Assert.Contains("src/new-impl.cs", committed);
        // Tasks-dir file (with Unicode) is NOT in the commit.
        var relTaskPath = $"llm-tasks/{tasksDirName}/task.md";
        Assert.DoesNotContain(relTaskPath, committed);

        // FindUncommittedAuthoredFilesAsync must also exclude it.
        var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
            repo.Root, preRunUntracked,
            tasksDir: "llm-tasks",
            CancellationToken.None);
        Assert.DoesNotContain(relTaskPath, missed);
    }

    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_FindsFileWithNarrowNoBreakSpace()
    {
        // The snapshot helper (git ls-files --others --exclude-standard)
        // must correctly capture files with U+202F in the name — no filtering
        // or escaping at the ls-files level should drop them.
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Disable git's core.quotePath so non-ASCII paths are emitted verbatim.
        TestGit.Run(repo.Root, "config", "core.quotePath", "false");

        var unicodeName = $"report{NarrowNoBreakSpace}2026-07-01.log";
        File.WriteAllText(Path.Combine(repo.Root, unicodeName), "log");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);

        Assert.Contains(unicodeName, snapshot);
    }

    // ── stub IGitInvoker that injects a stale path into ls-files output ─

    private sealed class StaleSnapshotGitInvoker(string stalePath) : IGitInvoker
    {
        private readonly GitInvoker _real = new();

        public async Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null,
            IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default,
            Action<string>? onActivity = null)
        {
            var args = arguments as IReadOnlyList<string> ?? arguments.ToList();

            // Intercept git ls-files --others to inject a stale path that
            // does not exist on disk, simulating a TOCTOU race where a file
            // vanished between the snapshot and git add.
            if (args is ["ls-files", "--others", "--exclude-standard"])
            {
                var result = await _real.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
                var injected = string.IsNullOrWhiteSpace(result.Output)
                    ? stalePath
                    : result.Output.TrimEnd() + "\n" + stalePath;
                return (result.ExitCode, injected, result.TimedOut);
            }

            return await _real.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
        }
    }
}
