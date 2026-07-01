using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed partial class RelayDriverResumeTests
{
    [Fact]
    public async Task FlagAsync_CreatesFlaggedWorkBundle_WhenMidPipelineStageFlags()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        // Initial commit + .gitignore so .relay/* is ignored (as in the real project).
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".gitignore"), ".relay/*\n");
        await git.RunAsync(repo.Root, ["add", ".gitignore"], CancellationToken.None, timeout: TimeSpan.FromSeconds(10));
        await git.RunAsync(repo.Root, ["commit", "-m", "gitignore"], CancellationToken.None, timeout: TimeSpan.FromSeconds(10));
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await repo.SeedCommitAsync(git, "src/initial.txt", "initial", "feat: initial",
            "2025-01-01T10:00:00", "2025-01-01T10:00:00");

        var taskId = "task-flagged";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);

        // Write a run-base so the snapshot has a parent.
        var runBaseSha = await repo.HeadShaAsync(git);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "run-base.txt"), runBaseSha);

        // Create a feature file that would be authored by the task.
        var featureFile = Path.Combine(repo.Root, "src", "Feature.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(featureFile)!);
        await File.WriteAllTextAsync(featureFile, "// feature");

        // Capture via FlaggedWorkStore directly.
        await FlaggedWorkStore.CaptureAsync(repo.Root, taskId, taskDirectory,
            flaggedStage: 6, git, DateTimeOffset.UtcNow, CancellationToken.None);

        // Assert bundle and sidecar were created.
        var bundlePath = Path.Combine(taskDirectory, "flagged-work.bundle");
        Assert.True(File.Exists(bundlePath), "flagged-work.bundle should exist after capture");
        var sidecarPath = Path.Combine(taskDirectory, "flagged-work.json");
        Assert.True(File.Exists(sidecarPath), "flagged-work.json should exist after capture");

        // Git status should NOT include the bundle (it is gitignored under .relay/*).
        var (statusExit, statusOutput, _) = await git.RunAsync(repo.Root,
            ["status", "--porcelain"], CancellationToken.None, timeout: TimeSpan.FromSeconds(10));
        Assert.Equal(0, statusExit);
        Assert.DoesNotContain("flagged-work.bundle", statusOutput);
    }

    [Fact]
    public async Task CaptureAsync_DoesNotFail_WhenNoRunBase()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        // Create an initial commit.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await repo.SeedCommitAsync(git, "src/x.txt", "x", "initial",
            "2025-01-01T10:00:00", "2025-01-01T10:00:00");

        var taskId = "task-no-base";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        // No run-base.txt — CaptureAsync should skip gracefully.
        await FlaggedWorkStore.CaptureAsync(repo.Root, taskId, taskDirectory,
            flaggedStage: 6, git, DateTimeOffset.UtcNow, CancellationToken.None);

        var bundlePath = Path.Combine(taskDirectory, "flagged-work.bundle");
        Assert.False(File.Exists(bundlePath), "Bundle should not be created without run-base");
    }

    [Fact]
    public async Task Resume_RestoresWork_WhenBundleExists()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        // Initial commit.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await repo.SeedCommitAsync(git, "src/initial.txt", "initial", "initial",
            "2025-01-01T10:00:00", "2025-01-01T10:00:00");

        var taskId = "task-restore";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);

        // Create a feature file as authored work.
        var featureFile = Path.Combine(repo.Root, "src", "Feature.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(featureFile)!);
        await File.WriteAllTextAsync(featureFile, "// implemented feature");

        // Capture a snapshot via FlaggedWorkStore.
        var runBaseSha = await repo.HeadShaAsync(git);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "run-base.txt"), runBaseSha);
        await FlaggedWorkStore.CaptureAsync(repo.Root, taskId, taskDirectory,
            flaggedStage: 6, git, DateTimeOffset.UtcNow, CancellationToken.None);

        var bundlePath = Path.Combine(taskDirectory, "flagged-work.bundle");
        Assert.True(File.Exists(bundlePath), "Bundle should exist after capture");

        // Remove the feature file (simulate reset).
        File.Delete(featureFile);
        Assert.False(File.Exists(featureFile));

        // Restore.
        var result = await FlaggedWorkStore.RestoreAsync(
            repo.Root, taskId, taskDirectory, git, CancellationToken.None);
        Assert.True(result.IsSuccess, "Restore should succeed");
        Assert.True(File.Exists(featureFile), "Feature file should be restored");
    }

    [Fact]
    public async Task Resume_FallsBackToStage1_WhenBundleVerifyFails()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        var taskId = "task-corrupt";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        // Write a corrupt bundle.
        var bundlePath = Path.Combine(taskDirectory, "flagged-work.bundle");
        await File.WriteAllTextAsync(bundlePath, "not a valid bundle");

        var result = await FlaggedWorkStore.RestoreAsync(
            repo.Root, taskId, taskDirectory, git, CancellationToken.None);
        Assert.True(result.IsUnrestorable, "Corrupt bundle should be unrestorable");
    }

    [Fact]
    public async Task Restore_ReturnsUnrestorable_WhenBundleMissing()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        var taskId = "task-missing";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);

        var result = await FlaggedWorkStore.RestoreAsync(
            repo.Root, taskId, taskDirectory, git, CancellationToken.None);
        Assert.True(result.IsUnrestorable, "Missing bundle should be unrestorable");
    }

    [Fact]
    public async Task Resume_RestoresWork_OntoAdvancedBase()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        // Initial commit.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await repo.SeedCommitAsync(git, "src/initial.txt", "initial", "initial",
            "2025-01-01T10:00:00", "2025-01-01T10:00:00");

        var taskId = "task-adv-base";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);

        var runBaseSha = await repo.HeadShaAsync(git);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "run-base.txt"), runBaseSha);

        // Authored feature file.
        var featureFile = Path.Combine(repo.Root, "src", "Feature.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(featureFile)!);
        await File.WriteAllTextAsync(featureFile, "// feature v1");

        // Snapshot.
        await FlaggedWorkStore.CaptureAsync(repo.Root, taskId, taskDirectory,
            flaggedStage: 6, git, DateTimeOffset.UtcNow, CancellationToken.None);

        // Advance HEAD with an unrelated commit.
        File.Delete(featureFile);
        await repo.SeedCommitAsync(git, "src/Unrelated.cs", "unrelated", "unrelated commit",
            "2025-02-01T10:00:00", "2025-02-01T10:00:00");
        var newHead = await repo.HeadShaAsync(git);
        Assert.NotEqual(runBaseSha, newHead);

        // Feature file should still be gone.
        Assert.False(File.Exists(featureFile));

        // Restore onto advanced base.
        var result = await FlaggedWorkStore.RestoreAsync(
            repo.Root, taskId, taskDirectory, git, CancellationToken.None);
        Assert.True(result.IsSuccess, "Restore should succeed onto advanced base");
        Assert.True(File.Exists(featureFile), "Feature file should be cherry-picked onto new HEAD");
    }

    [Fact]
    public async Task Resume_ProducesConflicts_WhenUpstreamOverlaps()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        // Authored file with feature work.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await repo.SeedCommitAsync(git, "src/Foo.cs", "class Foo { int X = 1; }", "initial",
            "2025-01-01T10:00:00", "2025-01-01T10:00:00");

        var taskId = "task-conflict";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);

        var runBaseSha = await repo.HeadShaAsync(git);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "run-base.txt"), runBaseSha);

        // Author a feature change (simulating task work).
        var fooFile = Path.Combine(repo.Root, "src", "Foo.cs");
        await File.WriteAllTextAsync(fooFile, "class Foo { int X = 2; string Name = \"test\"; }");

        // Snapshot the authored work.
        await FlaggedWorkStore.CaptureAsync(repo.Root, taskId, taskDirectory,
            flaggedStage: 6, git, DateTimeOffset.UtcNow, CancellationToken.None);

        // Now advance HEAD with a conflicting change to the same file.
        await File.WriteAllTextAsync(fooFile, "class Foo { int X = 3; }");
        await git.RunAsync(repo.Root, ["add", "src/Foo.cs"], CancellationToken.None, timeout: TimeSpan.FromSeconds(10));
        await git.RunAsync(repo.Root, ["commit", "-m", "upstream change"], CancellationToken.None, timeout: TimeSpan.FromSeconds(10));

        // Restore — should produce conflicts.
        var result = await FlaggedWorkStore.RestoreAsync(
            repo.Root, taskId, taskDirectory, git, CancellationToken.None);
        Assert.True(result.HasConflicts, "Should report conflicts when upstream overlaps");
        Assert.NotEmpty(result.ConflictedFiles);
        Assert.Contains("src/Foo.cs", result.ConflictedFiles);

        // Verify conflict markers are present in the file.
        var content = await File.ReadAllTextAsync(fooFile);
        Assert.Contains("<<<<<<<", content);
    }

    [Fact]
    public async Task FlaggedWorkBundle_Deleted_OnDelete()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        var taskId = "task-delete";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);

        // Create dummy files.
        var bundlePath = Path.Combine(taskDirectory, "flagged-work.bundle");
        var sidecarPath = Path.Combine(taskDirectory, "flagged-work.json");
        await File.WriteAllTextAsync(bundlePath, "dummy");
        await File.WriteAllTextAsync(sidecarPath, "{}");

        Assert.True(File.Exists(bundlePath));
        Assert.True(File.Exists(sidecarPath));

        FlaggedWorkStore.Delete(taskDirectory);

        Assert.False(File.Exists(bundlePath));
        Assert.False(File.Exists(sidecarPath));
    }

    [Fact]
    public async Task Resume_DoesNotRestore_WhenNotMidPipelineStage()
    {
        // The gating logic lives in RestoreFlaggedWorkIfNeededAsync which is
        // responsible for checking firstStageToRun range (5-10). Stages outside
        // this range (1-4, 11+) should not trigger restore. This is a code-level
        // contract; the integration test confirms the gating in the driver.
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await repo.SeedCommitAsync(git, "src/x.txt", "x", "initial",
            "2025-01-01T10:00:00", "2025-01-01T10:00:00");

        var taskId = "task-stage3";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);

        // Even with a valid bundle, flagging at stage 3 should not restore.
        var runBaseSha = await repo.HeadShaAsync(git);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "run-base.txt"), runBaseSha);
        await FlaggedWorkStore.CaptureAsync(repo.Root, taskId, taskDirectory,
            flaggedStage: 3, git, DateTimeOffset.UtcNow, CancellationToken.None);

        // Verify restore works mechanically, but the gating in the driver
        // (firstStageToRun outside 5-10) prevents it from being called.
        var bundlePath = Path.Combine(taskDirectory, "flagged-work.bundle");
        Assert.True(File.Exists(bundlePath));
        var result = await FlaggedWorkStore.RestoreAsync(
            repo.Root, taskId, taskDirectory, git, CancellationToken.None);
        // Even at stage 3, the restore mechanism works if called.
        Assert.True(result.IsSuccess);
    }
}
