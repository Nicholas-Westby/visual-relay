using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// Reset promises a flagged task a fresh start, and the working tree is where the
/// flagged run's edits live. A flagged single-task run leaves them there, and Reset —
/// the command an operator reaches for to clear them — did not reset the tree either.
/// It also archived the two files a resetter needs, after which nothing could put the
/// tree back at all. In one validation this showed as staged work surviving a reset,
/// which the operator had to clear by hand before the next run.
/// </summary>
public sealed class FlaggedTaskResetTests
{
    private static (TestRepository Repo, GitSimEngine Sim) FlaggedRepo(string taskId)
    {
        var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root);
        sim.Seed(repo.Root, "src/app.cs", "original\n");
        var runBase = sim.Commit(repo.Root, "base");

        var runDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "run-base.txt"), runBase);
        File.WriteAllText(Path.Combine(runDir, "pre-run-untracked.txt"), "kept.txt\n");
        File.WriteAllText(Path.Combine(runDir, "NEEDS-REVIEW"), "stage 12\nreason: commit gate rejected\n");
        return (repo, sim);
    }

    [Fact]
    public async Task ResetAsync_RestoresTrackedEdits_AndRemovesTheRunsUntrackedFiles()
    {
        var (repo, sim) = FlaggedRepo("t");
        using var _ = repo;
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "edited by the run\n");
        File.WriteAllText(Path.Combine(repo.Root, "stray.txt"), "authored by the run\n");
        File.WriteAllText(Path.Combine(repo.Root, "kept.txt"), "there before the run\n");

        var result = await FlaggedTaskReset.ResetAsync(repo.Root, "t", "llm-tasks", sim, CancellationToken.None);

        Assert.True(result.TreeReset);
        Assert.Null(result.Failure);
        Assert.False(File.Exists(Path.Combine(repo.Root, "stray.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Root, "kept.txt")));
        Assert.Contains("stray.txt", result.Removed);
    }

    [Fact]
    public async Task ResetAsync_ArchivesTheRunDirectory()
    {
        var (repo, sim) = FlaggedRepo("t");
        using var _ = repo;

        await FlaggedTaskReset.ResetAsync(repo.Root, "t", "llm-tasks", sim, CancellationToken.None);

        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".relay", "t")));
        Assert.Single(Directory.GetDirectories(Path.Combine(repo.Root, ".relay"), "t.reset-*"));
    }

    /// <summary>
    /// The capture runs BEFORE the archive move, because the move takes run-base.txt
    /// and pre-run-untracked.txt with it, and the operator may have edited since the
    /// flag. "It won't be lost" is the promise the modal makes.
    /// </summary>
    [Fact]
    public async Task ResetAsync_RecapturesTheBundleBeforeArchiving()
    {
        var (repo, sim) = FlaggedRepo("t");
        using var _ = repo;
        File.WriteAllText(Path.Combine(repo.Root, "src", "app.cs"), "edited after the flag\n");

        await FlaggedTaskReset.ResetAsync(repo.Root, "t", "llm-tasks", sim, CancellationToken.None);

        var archive = Directory.GetDirectories(Path.Combine(repo.Root, ".relay"), "t.reset-*").Single();
        Assert.True(File.Exists(Path.Combine(archive, "flagged-work.bundle")));
    }

    [Fact]
    public async Task ResetAsync_WithoutASnapshot_ResetsTrackedFilesAndReportsIt()
    {
        var (repo, sim) = FlaggedRepo("t");
        using var _ = repo;
        File.Delete(Path.Combine(repo.Root, ".relay", "t", "pre-run-untracked.txt"));
        File.WriteAllText(Path.Combine(repo.Root, "stray.txt"), "authored by the run\n");

        var result = await FlaggedTaskReset.ResetAsync(repo.Root, "t", "llm-tasks", sim, CancellationToken.None);

        Assert.True(result.SnapshotMissing);
        // Nothing is deleted on an unknown baseline: the file stays rather than risk
        // taking something the operator wrote.
        Assert.True(File.Exists(Path.Combine(repo.Root, "stray.txt")));
    }

    /// <summary>
    /// A git failure is reported, and the archive still happens: leaving the task
    /// flagged because git had a bad day helps nobody.
    /// </summary>
    [Fact]
    public async Task ResetAsync_ArchivesEvenWhenGitFails()
    {
        var (repo, _) = FlaggedRepo("t");
        using var owned = repo;

        var result = await FlaggedTaskReset.ResetAsync(
            owned.Root, "t", "llm-tasks", new ThrowingGitInvoker(), CancellationToken.None);

        Assert.False(result.TreeReset);
        Assert.NotNull(result.Failure);
        Assert.Single(Directory.GetDirectories(Path.Combine(owned.Root, ".relay"), "t.reset-*"));
    }

    /// <summary>
    /// While a run is active the RUNNING task owns the tree, so the command archives
    /// only and says which of the two things it did.
    /// </summary>
    [Fact]
    public void DescribeReset_WhileBusy_SaysWhyTheTreeWasLeft()
    {
        Assert.Contains(
            "left as is because a run is active",
            MainWindowViewModel.DescribeReset("t", null),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeReset_Idle_NamesTheRunBaseAndTheCount()
    {
        var text = MainWindowViewModel.DescribeReset(
            "t", new FlaggedTaskResetResult(TreeReset: true, Removed: ["a", "b", "c"], SnapshotMissing: false, Failure: null));

        Assert.Contains("working tree restored to the run base", text, StringComparison.Ordinal);
        Assert.Contains("removed 3 untracked files", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeReset_WithoutASnapshot_SaysTheUntrackedFilesWereKept()
    {
        var text = MainWindowViewModel.DescribeReset(
            "t", new FlaggedTaskResetResult(TreeReset: true, Removed: [], SnapshotMissing: true, Failure: null));

        Assert.Contains("untracked files kept: no pre-run snapshot", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeReset_AfterAGitFailure_NamesIt()
    {
        var text = MainWindowViewModel.DescribeReset(
            "t", new FlaggedTaskResetResult(TreeReset: false, Removed: [], SnapshotMissing: false, Failure: "git exploded"));

        Assert.Contains("tree reset failed: git exploded", text, StringComparison.Ordinal);
    }

    private sealed class ThrowingGitInvoker : IGitInvoker
    {
        public Task<(int ExitCode, string Output, bool TimedOut)> RunAsync(
            string rootPath, IEnumerable<string> arguments, CancellationToken cancellationToken,
            TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null,
            CancellationToken killToken = default, Action<string>? onActivity = null) =>
            throw new InvalidOperationException("git exploded");
    }
}
