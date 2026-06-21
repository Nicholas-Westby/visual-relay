using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class CompletionTimeResolverTests
{
    private sealed class FakeGitInvoker(
        Task<(int ExitCode, string Output, bool TimedOut)> result)
        : IGitInvoker
    {
        public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath, IEnumerable<string> arguments, CancellationToken cancellationToken,
            TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default, Action<string>? onActivity = null) =>
            result;
    }

    [Fact]
    public async Task Tier3_GitCommitTime_Success_ReturnsCommitterDate()
    {
        // Tier 3: when no .relay/<id>/ dir exists, the resolver falls through
        // tiers 1–2 and fires a git log for the committer date.
        using var scratch = ScratchRepo.Create();
        var git = new GitInvoker();
        await scratch.InitAsync(git);

        // Seed a commit touching a mock DONE markdown with a known committer date.
        var markdownPath = Path.Combine(scratch.Root, "llm-tasks", "DONE-git-task.md");
        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
        await File.WriteAllTextAsync(markdownPath, "# Git task\n");
        var committerDate = "2026-06-15T14:30:00-07:00";
        await scratch.SeedCommitAsync(git, "llm-tasks/DONE-git-task.md", "# Git task\n",
            "feat: complete git task",
            authorDate: "2026-06-15T14:30:00-07:00",
            committerDate: committerDate);

        // Create a task with no .relay dir (tiers 1–2 fail), no CompletedAt set.
        var task = new RelayTaskItem("git-task", markdownPath,
            Path.GetDirectoryName(markdownPath)!, false, [],
            IsArchived: true);

        var result = await CompletionTimeResolver.ResolveAsync(
            task, scratch.Root, git, CancellationToken.None);

        Assert.NotNull(result);
        // The returned timestamp must match the committer date of the seed commit.
        // git log --follow -1 --format=%cI returns ISO-8601 with offset.
        Assert.Equal(2026, result.Value.Year);
        Assert.Equal(6, result.Value.Month);
        Assert.Equal(15, result.Value.Day);
        Assert.Equal(14, result.Value.Hour);
        Assert.Equal(30, result.Value.Minute);
    }

    [Fact]
    public async Task Tier3_GitCommitTime_FollowsRenameIntoCompleted()
    {
        // Verify --follow resolves a rename from <id>.md → completed/batch-n/DONE-<id>.md.
        using var scratch = ScratchRepo.Create();
        var git = new GitInvoker();
        await scratch.InitAsync(git);

        // First commit: create the file at its original path.
        var originalPath = Path.Combine(scratch.Root, "llm-tasks", "follow-task.md");
        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        await File.WriteAllTextAsync(originalPath, "# Follow\n");
        await scratch.SeedCommitAsync(git, "llm-tasks/follow-task.md", "# Follow\n",
            "feat: create follow task",
            authorDate: "2026-06-10T10:00:00-07:00",
            committerDate: "2026-06-10T10:00:00-07:00");

        // Second commit: rename to DONE-* and move into completed/batch-1/.
        var completedDir = Path.Combine(scratch.Root, "llm-tasks", "completed", "batch-1");
        Directory.CreateDirectory(completedDir);
        var archivedPath = Path.Combine(completedDir, "DONE-follow-task.md");
        File.Move(originalPath, archivedPath);
        TestGit.Run(scratch.Root, "add", "-A");
        // Use explicit committer date for the retirement commit.
        var retirementDate = "2026-06-17T22:00:00-07:00";
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_COMMITTER_DATE"] = retirementDate,
        };
        var result = await git.RunAsync(scratch.Root, ["commit", "--no-verify", "-m", "chore: retire follow task"],
            CancellationToken.None, environment: env);
        Assert.Equal(0, result.ExitCode);

        var task = new RelayTaskItem("follow-task", archivedPath,
            completedDir, false, [],
            IsArchived: true);

        var resolved = await CompletionTimeResolver.ResolveAsync(
            task, scratch.Root, git, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(2026, resolved.Value.Year);
        Assert.Equal(6, resolved.Value.Month);
        Assert.Equal(17, resolved.Value.Day);
    }

    [Fact]
    public async Task Tier3_FallthroughOnNonZeroExit_FallsToTier4()
    {
        // When git returns non-zero, resolver falls to tier 4 (markdown mtime).
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("DONE-no-git", "# No git\n");
        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "DONE-no-git.md");
        var knownMtime = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(markdownPath, knownMtime.UtcDateTime);

        // No .relay dir → tiers 1–2 skip.
        var task = new RelayTaskItem("no-git", markdownPath,
            Path.GetDirectoryName(markdownPath)!, false, [],
            IsArchived: true);

        // Fake git returning exit code 128 (not a repo).
        var fakeGit = new FakeGitInvoker(
            Task.FromResult((128, "fatal: not a git repository", false)));

        var resolved = await CompletionTimeResolver.ResolveAsync(
            task, repo.Root, fakeGit, CancellationToken.None);

        // Must fall through to tier 4: markdown mtime.
        Assert.NotNull(resolved);
        Assert.Equal(knownMtime.Year, resolved.Value.Year);
        Assert.Equal(knownMtime.Month, resolved.Value.Month);
        Assert.Equal(knownMtime.Day, resolved.Value.Day);
    }

    [Fact]
    public async Task Tier3_FallthroughOnTimeout_FallsToTier4()
    {
        // When git times out, resolver falls to tier 4.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("DONE-timeout", "# Timeout\n");
        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "DONE-timeout.md");
        var knownMtime = new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(markdownPath, knownMtime.UtcDateTime);

        var task = new RelayTaskItem("timeout", markdownPath,
            Path.GetDirectoryName(markdownPath)!, false, [],
            IsArchived: true);

        var fakeGit = new FakeGitInvoker(
            Task.FromResult((-1, string.Empty, true))); // TimedOut=true

        var resolved = await CompletionTimeResolver.ResolveAsync(
            task, repo.Root, fakeGit, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(knownMtime.Year, resolved.Value.Year);
    }

    [Fact]
    public async Task Tier3_SkippedWhenGitInvokerNull_FallsToTier4()
    {
        // When gitInvoker is null, tier 3 is skipped entirely.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("DONE-null-git", "# Null git\n");
        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "DONE-null-git.md");
        var knownMtime = new DateTimeOffset(2026, 3, 15, 18, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(markdownPath, knownMtime.UtcDateTime);

        var task = new RelayTaskItem("null-git", markdownPath,
            Path.GetDirectoryName(markdownPath)!, false, [],
            IsArchived: true);

        var resolved = await CompletionTimeResolver.ResolveAsync(
            task, repo.Root, gitInvoker: null, CancellationToken.None);

        // Null gitInvoker → tier 3 skipped → tier 4 fires.
        Assert.NotNull(resolved);
        Assert.Equal(knownMtime.Year, resolved.Value.Year);
    }

    [Fact]
    public async Task Tier3_FallthroughOnEmptyOutput_FallsToTier4()
    {
        // When git returns exit 0 but empty output, fall to tier 4.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("DONE-empty-out", "# Empty\n");
        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "DONE-empty-out.md");
        var knownMtime = new DateTimeOffset(2026, 2, 1, 6, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(markdownPath, knownMtime.UtcDateTime);

        var task = new RelayTaskItem("empty-out", markdownPath,
            Path.GetDirectoryName(markdownPath)!, false, [],
            IsArchived: true);

        var fakeGit = new FakeGitInvoker(
            Task.FromResult((0, string.Empty, false)));

        var resolved = await CompletionTimeResolver.ResolveAsync(
            task, repo.Root, fakeGit, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(knownMtime.Year, resolved.Value.Year);
    }

    // ── Tier 2: .relay/<id>/ newest mtime ──────────────────────────────

    [Fact]
    public async Task Tier2_RelayDirNoReports_UsesNewestFileMtime()
    {
        // When .relay/<id>/ exists but has no report timestamps, 
        // tier 2 picks the newest file mtime under that directory.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("DONE-relay-dir", "# Relay dir\n");
        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "DONE-relay-dir.md");

        // Create .relay/<id>/ dir with files having known mtimes.
        var relayDir = Path.Combine(repo.Root, ".relay", "relay-dir");
        Directory.CreateDirectory(relayDir);
        var olderFile = Path.Combine(relayDir, "stage1-attempt1.report.json");
        await File.WriteAllTextAsync(olderFile, "{}"); // no timestamp field → tier 1 skips
        File.SetLastWriteTimeUtc(olderFile, new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc));

        var newerFile = Path.Combine(relayDir, "NEEDS-REVIEW");
        await File.WriteAllTextAsync(newerFile, "blocked");
        File.SetLastWriteTimeUtc(newerFile, new DateTime(2026, 6, 10, 15, 0, 0, DateTimeKind.Utc));

        // The task has no CompletedAt from tier 1 (report has no timestamp field).
        var task = new RelayTaskItem("relay-dir", markdownPath,
            Path.GetDirectoryName(markdownPath)!, false, [],
            IsArchived: true);

        var resolved = await CompletionTimeResolver.ResolveAsync(
            task, repo.Root, gitInvoker: null, CancellationToken.None);

        // Should use the newest file mtime under .relay/relay-dir/ (the NEEDS-REVIEW file).
        Assert.NotNull(resolved);
        Assert.Equal(2026, resolved.Value.Year);
        Assert.Equal(6, resolved.Value.Month);
        Assert.Equal(10, resolved.Value.Day);
        Assert.Equal(15, resolved.Value.Hour);
    }

    [Fact]
    public async Task Tier2_EmptyRelayDir_FallsToNextTier()
    {
        // When .relay/<id>/ exists but is empty, tier 2 returns null → tier 3 or 4 fires.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("DONE-empty-relay", "# Empty relay\n");
        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "DONE-empty-relay.md");
        var knownMtime = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(markdownPath, knownMtime.UtcDateTime);

        // Create an empty .relay/<id>/ dir.
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay", "empty-relay"));

        var task = new RelayTaskItem("empty-relay", markdownPath,
            Path.GetDirectoryName(markdownPath)!, false, [],
            IsArchived: true);

        var resolved = await CompletionTimeResolver.ResolveAsync(
            task, repo.Root, gitInvoker: null, CancellationToken.None);

        // Empty .relay dir → tier 2 nothing → falls to tier 4 (no git).
        Assert.NotNull(resolved);
        Assert.Equal(knownMtime.Year, resolved.Value.Year);
    }

    // ── Tier 4: markdown mtime fallback ────────────────────────────────

    [Fact]
    public async Task Tier4_NoRelayDirNoGit_UsesMarkdownMtime()
    {
        // When there is no .relay/<id>/ dir and no git invoker,
        // the resolver falls through to the markdown's last-write time.
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("DONE-bare", "# Bare task\n");
        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "DONE-bare.md");
        var knownMtime = new DateTimeOffset(2026, 7, 4, 14, 0, 0, TimeSpan.Zero);
        File.SetLastWriteTimeUtc(markdownPath, knownMtime.UtcDateTime);

        var task = new RelayTaskItem("bare", markdownPath,
            Path.GetDirectoryName(markdownPath)!, false, [],
            IsArchived: true);

        var resolved = await CompletionTimeResolver.ResolveAsync(
            task, repo.Root, gitInvoker: null, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(knownMtime.Year, resolved.Value.Year);
        Assert.Equal(knownMtime.Month, resolved.Value.Month);
        Assert.Equal(knownMtime.Day, resolved.Value.Day);
    }
}
