using VisualRelay.Core.Execution;
using static VisualRelay.Tests.GitCommitterGitSimSetup;

namespace VisualRelay.Tests;

public sealed class GitCommitterAutoIncludeResilienceTests
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
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, sim, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Create a real authored file AND a sibling whose path we will
        // inject into the snapshot as "stale" (it never exists on disk).
        Write(repo, "src/app.cs", "updated");
        Write(repo, "tests-extra.cs", "// real");

        var staleRelPath = "ghost-file.cs";
        var staleInvoker = new StaleSnapshotGitInvoker(staleRelPath, sim);

        var manifest = new[] { "src/app.cs" };

        var result = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked,
            tasksDir: null,
            cancellationToken: CancellationToken.None,
            gitInvoker: staleInvoker, timeProvider: TimeProvider.System);

        Assert.True(result.Success, result.Error);
        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
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
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, sim, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // Real git needs core.quotePath=false to emit non-ASCII paths verbatim
        // instead of C-quoted; GitSim never quotes/escapes paths in any output
        // (no command handler reads GitSimContext.QuotePath), so no equivalent
        // setup step is needed here.

        Write(repo, "src/app.cs", "updated");

        // File name with U+202F between "05" and "AM" — mirrors the real
        // screenshot filename that triggered the original failure.
        var unicodeName = $"Screenshot 2026-07-01 at 9.59.05{NarrowNoBreakSpace}AM.png";
        Write(repo, unicodeName, "fake screenshot");

        var manifest = new[] { "src/app.cs" };

        var result = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked,
            tasksDir: null,
            sim, CancellationToken.None, timeProvider: TimeProvider.System);

        Assert.True(result.Success, result.Error);
        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        Assert.Contains(unicodeName, committed);
    }

    [Fact]
    public async Task CommitAsync_ExcludesTasksDirFileWithUnicodeInPath()
    {
        // A file dropped under the tasks dir whose path contains U+202F
        // must be excluded from auto-include.  The tasks-dir guard must work
        // regardless of Unicode characters in the path.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed");

        var preRunUntracked = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, sim, CancellationToken.None);
        Assert.Empty(preRunUntracked);

        // (No core.quotePath setup needed — see comment above; GitSim always
        // emits literal paths.)

        Write(repo, "src/app.cs", "updated");
        Write(repo, "src/new-impl.cs", "// genuinely authored");

        // Tasks-dir file with U+202F in the directory name.
        var tasksDirName = $"task{NarrowNoBreakSpace}dir";
        Write(repo, $"llm-tasks/{tasksDirName}/task.md", "# user task");

        var manifest = new[] { "src/app.cs" };

        var commit = await GitCommitter.CommitAsync(
            repo.Root, "task", "abc", ["feat: x"], manifest, [],
            commitToken: null, preRunUntracked,
            tasksDir: "llm-tasks",
            sim, CancellationToken.None, timeProvider: TimeProvider.System);
        Assert.True(commit.Success, commit.Error);

        var committed = sim.FilesInCommit(repo.Root, sim.Head(repo.Root)!);
        // Genuinely authored file outside tasks dir IS auto-included.
        Assert.Contains("src/new-impl.cs", committed);
        // Tasks-dir file (with Unicode) is NOT in the commit.
        var relTaskPath = $"llm-tasks/{tasksDirName}/task.md";
        Assert.DoesNotContain(relTaskPath, committed);

        // FindUncommittedAuthoredFilesAsync must also exclude it.
        var missed = await GitCommitter.FindUncommittedAuthoredFilesAsync(
            repo.Root, preRunUntracked,
            tasksDir: "llm-tasks",
            sim, CancellationToken.None);
        Assert.DoesNotContain(relTaskPath, missed);
    }

    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_FindsFileWithNarrowNoBreakSpace()
    {
        // The snapshot helper (git ls-files --others --exclude-standard)
        // must correctly capture files with U+202F in the name — no filtering
        // or escaping at the ls-files level should drop them.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "content");
        sim.Commit(repo.Root, "chore: seed");

        // (No core.quotePath setup needed — see comment above; GitSim always
        // emits literal paths.)

        var unicodeName = $"report{NarrowNoBreakSpace}2026-07-01.log";
        Write(repo, unicodeName, "log");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, sim, CancellationToken.None);

        Assert.Contains(unicodeName, snapshot);
    }

    // ── C-quoted output decoding ───────────────────────────────────────

    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_DecodesCQuotedPath()
    {
        // When real git (core.quotePath=true, the default) emits a C-quoted
        // path for non-ASCII filenames, CaptureUntrackedSnapshotAsync must
        // decode it to the real path before storing it in the snapshot set.
        var (sim, repo) = NewRepo();
        using var _ = repo;
        sim.Seed(repo.Root, "src/app.cs", "old");
        sim.Commit(repo.Root, "chore: seed");

        // The real filename on disk contains U+202F
        var realName = $"Screenshot 2026-07-15 at 9.37.05{NarrowNoBreakSpace}PM.png";
        Write(repo, realName, "fake screenshot");

        // Simulate git outputting the C-quoted form (what real git does by default)
        var quotedName = "\"Screenshot 2026-07-15 at 9.37.05\\342\\200\\257PM.png\"";
        var quotedInvoker = new QuotedPathGitInvoker(sim, quotedName);

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, quotedInvoker, CancellationToken.None);

        // The snapshot must contain the DECODED (real) path, not the C-quoted form.
        Assert.Contains(realName, snapshot);
        Assert.DoesNotContain(quotedName, snapshot);
    }

    // ── stub IGitInvoker that replaces ls-files output with C-quoted paths ─

    private sealed class QuotedPathGitInvoker(
        IGitInvoker inner,
        string quotedOutput) : IGitInvoker
    {
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

            // Intercept the target git command (e.g. ls-files --others --exclude-standard
            // with -c core.quotePath=false prefix) and return C-quoted output instead,
            // simulating real git with core.quotePath=true.
            if (args is ["-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard"])
            {
                // Run the real command to get authentic exit code / error output,
                // but replace the path output with the quoted version.
                var realResult = await inner.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
                return (realResult.ExitCode, quotedOutput, realResult.TimedOut);
            }

            return await inner.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
        }
    }

    // ── stub IGitInvoker that injects a stale path into ls-files output ─

    private sealed class StaleSnapshotGitInvoker(string stalePath, IGitInvoker inner) : IGitInvoker
    {
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
            if (args is ["-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard"])
            {
                var result = await inner.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
                var injected = string.IsNullOrWhiteSpace(result.Output)
                    ? stalePath
                    : result.Output.TrimEnd() + "\n" + stalePath;
                return (result.ExitCode, injected, result.TimedOut);
            }

            return await inner.RunAsync(rootPath, args, cancellationToken, timeout, environment, killToken, onActivity);
        }
    }
}
