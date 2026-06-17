using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Regression guards for the rollback delegate returned by
/// <see cref="TaskCompletionArchive.RetireAsync"/> on the no-batch
/// direct-under-<c>completed/</c> branch (flat and nested shapes).
/// These verify that a commit failure restores the task to its original
/// location so it can be retried.
/// </summary>
public sealed class TaskCompletionArchiveNoBatchRollbackTests
{
    // Minimal config that enables archiveOnDone with no batch dirs present.
    private static RelayConfig MakeArchiveConfig() => new(
        TasksDir: "llm-tasks",
        TestCommand: "dotnet test",
        TestFileCommand: "dotnet test {files}",
        LogSources: [],
        TierProfiles: new Dictionary<string, string>(),
        MaxVerifyLoops: 5,
        MaxStageFailures: 3,
        MaxTurns: 200,
        BaselineVerify: true,
        ArchiveOnDone: true,
        SubagentTimeoutMilliseconds: 1_200_000,
        TestTimeoutMilliseconds: 300_000,
        FirstOutputTimeoutMsByTier: new Dictionary<string, int>(),
        FirstOutputTimeoutMs: 660_000,
        MaxStallRetries: 2);

    [Fact]
    public void RetireAsync_FlatNoBatch_RollbackRestoresOriginalLocation()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("ship-status", "# Ship status\n");
        var config = MakeArchiveConfig();

        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "ship-status.md");
        var task = new RelayTaskItem(
            Id: "ship-status",
            MarkdownPath: markdownPath,
            TaskDirectory: Path.Combine(repo.Root, "llm-tasks"),
            IsNested: false,
            SiblingPaths: []);

        var result = TaskCompletionArchive.RetireAsync(repo.Root, config, "ship-status", task);

        Assert.NotNull(result);
        Assert.NotNull(result.Rollback);

        // After RetireAsync: file should be at completed/DONE-ship-status.md.
        var archivePath = Path.Combine(repo.Root, "llm-tasks", "completed", "DONE-ship-status.md");
        Assert.True(File.Exists(archivePath), $"expected archive at {archivePath}");
        Assert.False(File.Exists(markdownPath), "original must be gone after retire");

        // Simulate a commit failure by invoking the rollback delegate.
        result.Rollback();

        // After rollback: original is restored; archived copy is gone.
        Assert.True(File.Exists(markdownPath), $"original must be restored at {markdownPath}");
        Assert.False(File.Exists(archivePath), "archived copy must be removed after rollback");
    }

    [Fact]
    public void RetireAsync_NestedNoBatch_RollbackRestoresOriginalLocation()
    {
        using var repo = TestRepository.Create();
        repo.WriteNestedTask("ship-status", "# Ship status\n",
            ("notes.txt", "some notes"), ("diagram.png", "fake png"));
        var config = MakeArchiveConfig();

        var taskDir = Path.Combine(repo.Root, "llm-tasks", "ship-status");
        var markdownPath = Path.Combine(taskDir, "ship-status.md");
        var task = new RelayTaskItem(
            Id: "ship-status",
            MarkdownPath: markdownPath,
            TaskDirectory: taskDir,
            IsNested: true,
            SiblingPaths: [
                Path.Combine(taskDir, "notes.txt"),
                Path.Combine(taskDir, "diagram.png"),
            ]);

        var result = TaskCompletionArchive.RetireAsync(repo.Root, config, "ship-status", task);

        Assert.NotNull(result);
        Assert.NotNull(result.Rollback);

        // After RetireAsync: directory moved to completed/ship-status/.
        var archiveDir = Path.Combine(repo.Root, "llm-tasks", "completed", "ship-status");
        Assert.True(Directory.Exists(archiveDir), $"expected archive dir at {archiveDir}");
        Assert.True(File.Exists(Path.Combine(archiveDir, "DONE-ship-status.md")));
        Assert.False(Directory.Exists(taskDir), "original task dir must be gone after retire");

        // Simulate a commit failure by invoking the rollback delegate.
        result.Rollback();

        // After rollback: original directory is restored; archived directory is gone.
        Assert.True(Directory.Exists(taskDir), $"original task dir must be restored at {taskDir}");
        Assert.True(File.Exists(markdownPath), "original markdown must be inside restored dir");
        Assert.False(Directory.Exists(archiveDir), "archived dir must be removed after rollback");
    }
}
