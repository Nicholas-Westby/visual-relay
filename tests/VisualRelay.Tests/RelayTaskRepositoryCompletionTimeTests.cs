using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

public sealed class RelayTaskRepositoryCompletionTimeTests
{
    // ── Completion-time ordering (tiers 1, 2, 4) ───────────────────────

    [Fact]
    public async Task ListCompletedAsync_Tier1_RunMetadataTimestamps_OrdersNewestFirst()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var batch1 = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-1");
        Directory.CreateDirectory(batch1);
        await File.WriteAllTextAsync(Path.Combine(batch1, "DONE-older.md"), "# Older\n");
        RelayTaskRepositoryTests.WriteReportWithTimestamp(repo.Root, "older", 1,
            "2026-05-01T10:00:00+00:00", 1.0, 1000);

        var batch2 = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2");
        Directory.CreateDirectory(batch2);
        await File.WriteAllTextAsync(Path.Combine(batch2, "DONE-newer.md"), "# Newer\n");
        RelayTaskRepositoryTests.WriteReportWithTimestamp(repo.Root, "newer", 1,
            "2026-06-15T14:00:00+00:00", 1.0, 1000);

        var tasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();

        Assert.Equal(["newer", "older"], tasks.Select(t => t.Id));
        Assert.True(tasks[0].CompletedAt > tasks[1].CompletedAt);
    }

    [Fact]
    public async Task ListCompletedAsync_Tier2_RelayDirNoReport_UsesFileMtime()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var completed = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-1");
        Directory.CreateDirectory(completed);

        await File.WriteAllTextAsync(Path.Combine(completed, "DONE-t2.md"), "# T2\n");
        var relayDir = Path.Combine(repo.Root, ".relay", "t2");
        Directory.CreateDirectory(relayDir);

        var subDir = Path.Combine(relayDir, "traces");
        Directory.CreateDirectory(subDir);
        var traceFile = Path.Combine(subDir, "session.jsonl");
        await File.WriteAllTextAsync(traceFile, "{}");
        File.SetLastWriteTimeUtc(traceFile, new DateTime(2026, 5, 15, 9, 0, 0, DateTimeKind.Utc));

        await File.WriteAllTextAsync(Path.Combine(relayDir, "NEEDS-REVIEW"), "ok");
        File.SetLastWriteTimeUtc(
            Path.Combine(relayDir, "NEEDS-REVIEW"),
            new DateTime(2026, 5, 15, 8, 0, 0, DateTimeKind.Utc));

        var tasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();

        var task = Assert.Single(tasks);
        Assert.Equal("t2", task.Id);
        Assert.NotNull(task.CompletedAt);
        Assert.Equal(9, task.CompletedAt.Value.Hour);
    }

    [Fact]
    public async Task ListCompletedAsync_Tier4_NoRelayDirNoGit_UsesMarkdownMtime()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var completed = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-1");
        Directory.CreateDirectory(completed);

        var markdownPath = Path.Combine(completed, "DONE-t4.md");
        await File.WriteAllTextAsync(markdownPath, "# T4\n");
        var knownMtime = new DateTime(2026, 3, 20, 16, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(markdownPath, knownMtime);

        var tasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();

        var task = Assert.Single(tasks);
        Assert.Equal("t4", task.Id);
        Assert.NotNull(task.CompletedAt);
        Assert.Equal(2026, task.CompletedAt.Value.Year);
        Assert.Equal(3, task.CompletedAt.Value.Month);
        Assert.Equal(20, task.CompletedAt.Value.Day);
    }

    [Fact]
    public async Task ListCompletedAsync_TieBreak_OrdersByIdWhenCompletedAtIdentical()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);

        var sameMtime = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);

        var completedBatch1 = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-1");
        var completedBatch2 = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2");
        Directory.CreateDirectory(completedBatch1);
        Directory.CreateDirectory(completedBatch2);

        var pathB = Path.Combine(completedBatch1, "DONE-bravo.md");
        var pathA = Path.Combine(completedBatch2, "DONE-alpha.md");
        await File.WriteAllTextAsync(pathB, "# Bravo\n");
        await File.WriteAllTextAsync(pathA, "# Alpha\n");
        File.SetLastWriteTimeUtc(pathB, sameMtime);
        File.SetLastWriteTimeUtc(pathA, sameMtime);

        var tasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();

        Assert.Equal(2, tasks.Count);
        Assert.NotNull(tasks[0].CompletedAt);
        Assert.NotNull(tasks[1].CompletedAt);
        Assert.Equal("alpha", tasks[0].Id);
        Assert.Equal("bravo", tasks[1].Id);
    }

    [Fact]
    public async Task ListCompletedAsync_WithGitInvoker_Tier3ProbesForUnresolvedTasks()
    {
        using var scratch = ScratchRepo.Create();
        var git = new GitInvoker();
        await scratch.InitAsync(git);

        Directory.CreateDirectory(Path.Combine(scratch.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(scratch.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [] }""");

        var batch1 = Path.Combine(scratch.Root, "llm-tasks", "completed", "batch-1");
        var batch2 = Path.Combine(scratch.Root, "llm-tasks", "completed", "batch-2");
        Directory.CreateDirectory(batch1);
        Directory.CreateDirectory(batch2);

        var earlierPath = Path.Combine(batch1, "DONE-earlier.md");
        await File.WriteAllTextAsync(earlierPath, "# Earlier\n");
        await scratch.SeedCommitAsync(git, "llm-tasks/completed/batch-1/DONE-earlier.md",
            "# Earlier\n", "feat: earlier",
            authorDate: "2026-06-01T10:00:00-07:00",
            committerDate: "2026-06-01T10:00:00-07:00");

        var laterPath = Path.Combine(batch2, "DONE-later.md");
        await File.WriteAllTextAsync(laterPath, "# Later\n");
        await scratch.SeedCommitAsync(git, "llm-tasks/completed/batch-2/DONE-later.md",
            "# Later\n", "feat: later",
            authorDate: "2026-06-10T18:00:00-07:00",
            committerDate: "2026-06-10T18:00:00-07:00");

        var tasks = await new RelayTaskRepository(scratch.Root, git).ListCompletedAsync();

        Assert.Equal(2, tasks.Count);
        Assert.Equal("later", tasks[0].Id);
        Assert.Equal("earlier", tasks[1].Id);
    }
}
