using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

// Snapshot-helper tests, split out of GitCommitterAutoIncludeTests.cs to keep
// each file under the 300-line guard. Shares the partial class's git helpers.
public sealed partial class GitCommitterAutoIncludeTests
{
    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_ReturnsEmptySetWhenClean()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot);
    }

    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_ExcludesGitignoredFiles()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        File.WriteAllText(Path.Combine(repo.Root, ".gitignore"), "*.log\n");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Create an untracked file that matches .gitignore.
        File.WriteAllText(Path.Combine(repo.Root, "debug.log"), "ignored");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.DoesNotContain("debug.log", snapshot);
    }

    [Fact]
    public async Task CaptureUntrackedSnapshotAsync_ReturnsUntrackedFilesOutsideGitignore()
    {
        using var repo = TestRepository.Create();
        await InitGitRepo(repo.Root);
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        Directory.CreateDirectory(Path.Combine(repo.Root, "scratch"));
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "content");
        await StageAndCommitSeed(repo.Root, "chore: seed");

        // Create an untracked scratch file.
        File.WriteAllText(Path.Combine(repo.Root, "scratch", "notes.txt"), "notes");

        var snapshot = await GitCommitter.CaptureUntrackedSnapshotAsync(
            repo.Root, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Contains("scratch/notes.txt", snapshot);
    }
}
