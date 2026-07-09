using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class RelayDriverResumeFlaggedWork3Tests
{
    /// <summary>
    /// When .relay/config.json is tracked at the run base, a capture→restore
    /// round-trip must NOT delete it. (Defect: the old <c>git rm --cached -r --
    /// .relay/</c> stripped ALL .relay/ entries from the temp index, recording
    /// tracked files as deletions in the snapshot tree.)
    /// </summary>
    [Fact]
    public async Task CaptureRestore_RoundTrip_PreservesTrackedRelayFiles()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        // Commit a tracked .relay/config.json at the run base.
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        var configContent = "{\"testCmd\":\"true\"}";
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), configContent);
        await git.RunAsync(repo.Root, ["add", ".relay/config.json"], CancellationToken.None,
            timeout: TimeSpan.FromSeconds(10));
        await git.RunAsync(repo.Root, ["commit", "-m", "feat: initial"], CancellationToken.None,
            timeout: TimeSpan.FromSeconds(10));

        var taskId = "task-relay-files";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);

        var runBaseSha = await repo.HeadShaAsync(git);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "run-base.txt"), runBaseSha);

        // Author a feature file (simulating task work).
        var featureFile = Path.Combine(repo.Root, "src", "Feature.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(featureFile)!);
        await File.WriteAllTextAsync(featureFile, "// feature");

        // Capture a snapshot.
        await FlaggedWorkStore.CaptureAsync(repo.Root, taskId, taskDirectory,
            flaggedStage: 6, git, DateTimeOffset.UtcNow, CancellationToken.None);

        var bundlePath = Path.Combine(taskDirectory, "flagged-work.bundle");
        Assert.True(File.Exists(bundlePath), "Bundle should exist after capture");

        // Remove the feature file (simulate a clean checkout of base).
        File.Delete(featureFile);
        Assert.False(File.Exists(featureFile));

        // Restore.
        var result = await FlaggedWorkStore.RestoreAsync(
            repo.Root, taskId, taskDirectory, git, CancellationToken.None);
        Assert.True(result.IsSuccess, "Restore should succeed");

        // Tracked .relay/config.json must survive byte-for-byte.
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        Assert.True(File.Exists(configPath),
            ".relay/config.json should survive restore — it was tracked at the run base");
        var restoredConfig = await File.ReadAllTextAsync(configPath);
        Assert.Equal(configContent, restoredConfig);

        // The task's real edit must also be restored.
        Assert.True(File.Exists(featureFile), "Feature file should be restored");
        Assert.Equal("// feature", await File.ReadAllTextAsync(featureFile));
    }

    /// <summary>
    /// Under <c>core.fileMode=false</c>, a capture→restore round-trip must
    /// preserve the executable bit (100755) for scripts that were executable at
    /// the run base. (Defect: the old empty temp index + <c>git add -A</c>
    /// recorded every path as 100644 when core.fileMode=false.)
    /// </summary>
    [Fact]
    public async Task CaptureRestore_RoundTrip_PreservesExecutableMode_UnderCoreFileModeFalse()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        // Set core.fileMode=false — the defect scenario.
        await git.RunAsync(repo.Root, ["config", "core.fileMode", "false"], CancellationToken.None,
            timeout: TimeSpan.FromSeconds(10));

        // Create an executable script and force 100755 in the index.
        var scriptPath = Path.Combine(repo.Root, "script.sh");
        await File.WriteAllTextAsync(scriptPath, "#!/bin/sh\necho hi\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await git.RunAsync(repo.Root, ["add", "script.sh"], CancellationToken.None,
            timeout: TimeSpan.FromSeconds(10));
        // Force the executable bit in the index — under core.fileMode=false,
        // git add alone would record 100644.
        await git.RunAsync(repo.Root, ["update-index", "--chmod=+x", "script.sh"],
            CancellationToken.None, timeout: TimeSpan.FromSeconds(10));
        await git.RunAsync(repo.Root, ["commit", "-m", "feat: initial"], CancellationToken.None,
            timeout: TimeSpan.FromSeconds(10));

        // Verify the base has 100755.
        var (_, baseLs, _) = await git.RunAsync(repo.Root, ["ls-files", "--stage", "script.sh"],
            CancellationToken.None, timeout: TimeSpan.FromSeconds(10));
        Assert.True(baseLs.Trim().StartsWith("100755", StringComparison.Ordinal),
            "Base commit must record script.sh as 100755");

        var runBaseSha = await repo.HeadShaAsync(git);

        var taskId = "task-exec-mode";
        var taskDirectory = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "run-base.txt"), runBaseSha);

        // Author a feature file (simulating task work).
        var featureFile = Path.Combine(repo.Root, "src", "Feature.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(featureFile)!);
        await File.WriteAllTextAsync(featureFile, "// feature");

        // Capture a snapshot.
        await FlaggedWorkStore.CaptureAsync(repo.Root, taskId, taskDirectory,
            flaggedStage: 6, git, DateTimeOffset.UtcNow, CancellationToken.None);

        var bundlePath = Path.Combine(taskDirectory, "flagged-work.bundle");
        Assert.True(File.Exists(bundlePath), "Bundle should exist after capture");

        // Remove the feature file (simulate a clean checkout of base).
        File.Delete(featureFile);
        Assert.False(File.Exists(featureFile));

        // Restore.
        var result = await FlaggedWorkStore.RestoreAsync(
            repo.Root, taskId, taskDirectory, git, CancellationToken.None);
        Assert.True(result.IsSuccess, "Restore should succeed");

        // The script's index mode must still be 100755.
        var (_, lsOutput, _) = await git.RunAsync(repo.Root, ["ls-files", "--stage", "script.sh"],
            CancellationToken.None, timeout: TimeSpan.FromSeconds(10));
        Assert.True(lsOutput.Trim().StartsWith("100755", StringComparison.Ordinal),
            "Executable bit (100755) must survive capture+restore under core.fileMode=false");

        // The task's real edit must also be restored.
        Assert.True(File.Exists(featureFile), "Feature file should be restored");
        Assert.Equal("// feature", await File.ReadAllTextAsync(featureFile));
    }
}
