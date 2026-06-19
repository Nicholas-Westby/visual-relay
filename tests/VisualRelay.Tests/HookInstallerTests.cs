using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

public sealed class HookInstallerTests
{
    [Fact]
    public async Task InstallAsync_CreatesPreCommitHook_InDefaultHooksDir()
    {
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");

        var result = await HookInstaller.InstallAsync(repo.Root, CancellationToken.None);

        Assert.True(result.Installed);
        var hookPath = Path.Combine(repo.Root, ".git", "hooks", "pre-commit");
        Assert.True(File.Exists(hookPath));
        var content = await File.ReadAllTextAsync(hookPath);
        Assert.Contains("# Visual Relay pre-commit hook", content);
        // Must be executable on Unix.
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(hookPath);
            Assert.True((mode & UnixFileMode.UserExecute) != 0,
                "installed pre-commit hook is not user-executable");
        }
    }

    [Fact]
    public async Task InstallAsync_NonGitFolder_ReturnsWarning_WithoutCreatingGitDir()
    {
        using var repo = TestRepository.Create(); // a plain dir, NOT a git repo

        var result = await HookInstaller.InstallAsync(repo.Root, CancellationToken.None);

        Assert.False(result.Installed);
        Assert.NotNull(result.Warning);
        // Must not fabricate a bogus .git/hooks dir where the hook can never run.
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".git")));
    }

    [Fact]
    public async Task InstallAsync_IsIdempotent()
    {
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");

        var first = await HookInstaller.InstallAsync(repo.Root, CancellationToken.None);
        Assert.True(first.Installed);

        var second = await HookInstaller.InstallAsync(repo.Root, CancellationToken.None);
        Assert.True(second.Installed, "re-installing should still report Installed=true");

        var hookPath = Path.Combine(repo.Root, ".git", "hooks", "pre-commit");
        Assert.True(File.Exists(hookPath));
        var content = await File.ReadAllTextAsync(hookPath);
        // Marker should appear exactly once (no duplication).
        var markerCount = content.Split("# Visual Relay pre-commit hook").Length - 1;
        Assert.Equal(1, markerCount);
    }

    [Fact]
    public async Task InstallAsync_RespectsCoreHooksPath()
    {
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");
        var customDir = Path.Combine(repo.Root, "my-hooks");
        Directory.CreateDirectory(customDir);
        TestGit.Run(repo.Root, "config", "core.hooksPath", "my-hooks");

        var result = await HookInstaller.InstallAsync(repo.Root, CancellationToken.None);

        Assert.True(result.Installed);
        var hookPath = Path.Combine(customDir, "pre-commit");
        Assert.True(File.Exists(hookPath));
        // The default .git/hooks should NOT have the hook.
        Assert.False(File.Exists(Path.Combine(repo.Root, ".git", "hooks", "pre-commit")));
    }

    [Fact]
    public async Task InstallAsync_PreservesForeignHook()
    {
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");
        var hooksDir = Path.Combine(repo.Root, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "pre-commit");
        var foreignContent = "#!/bin/sh\necho 'my custom hook'\nexit 0\n";
        await File.WriteAllTextAsync(hookPath, foreignContent);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var result = await HookInstaller.InstallAsync(repo.Root, CancellationToken.None);

        Assert.False(result.Installed, "should not overwrite a foreign pre-commit hook");
        Assert.NotNull(result.Warning);
        Assert.Contains("pre-commit", result.Warning, StringComparison.OrdinalIgnoreCase);
        var content = await File.ReadAllTextAsync(hookPath);
        Assert.Equal(foreignContent, content); // Original content preserved byte-for-byte.
    }

    [Fact]
    public async Task InstallAsync_OverwritesVisualRelayOwnedHook()
    {
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");
        var hooksDir = Path.Combine(repo.Root, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, "pre-commit");
        // An older version of the VR hook (has the marker comment).
        var oldContent = "#!/bin/sh\n# Visual Relay pre-commit hook\n# v1.0-old\nexit 0\n";
        await File.WriteAllTextAsync(hookPath, oldContent);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var result = await HookInstaller.InstallAsync(repo.Root, CancellationToken.None);

        Assert.True(result.Installed, "should overwrite a VR-owned pre-commit hook");
        var content = await File.ReadAllTextAsync(hookPath);
        Assert.Contains("# Visual Relay pre-commit hook", content);
        Assert.DoesNotContain("v1.0-old", content);
        Assert.DoesNotContain("my custom", content);
    }

    [Fact]
    public async Task InstallAsync_LeavesCommitMsgUntouched()
    {
        using var repo = TestRepository.Create();
        TestGit.Run(repo.Root, "init");
        var hooksDir = Path.Combine(repo.Root, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var commitMsgPath = Path.Combine(hooksDir, "commit-msg");
        var commitMsgContent = "#!/bin/sh\necho 'custom commit-msg'\nexit 0\n";
        await File.WriteAllTextAsync(commitMsgPath, commitMsgContent);

        await HookInstaller.InstallAsync(repo.Root, CancellationToken.None);

        // commit-msg must be left alone (different file, not our concern).
        Assert.True(File.Exists(commitMsgPath));
        Assert.Equal(commitMsgContent, await File.ReadAllTextAsync(commitMsgPath));
    }
}
