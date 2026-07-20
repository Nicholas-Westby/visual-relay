using System.Diagnostics;

namespace VisualRelay.Tests;

public sealed class PreCommitHookIdentityStripTests
{
    [Fact]
    public void StripIdentity_InfectedRepo_StripsLocalIdentityAndWarns()
    {
        using var globalCfg = WriteGlobalGitConfig("Global User", "global@example.test");
        using var repo = PreCommitHookTests.CreateRepoWithHook(configureLocalIdentity: false);

        // Inject local identity (simulates the infected state).
        PreCommitHookTests.RunGit(repo.Root, ["config", "--local", "user.name", "Evil User"]);
        PreCommitHookTests.RunGit(repo.Root, ["config", "--local", "user.email", "evil@example.test"]);

        File.WriteAllText(Path.Combine(repo.Root, "test.txt"), "hello");
        PreCommitHookTests.RunGit(repo.Root, ["add", "test.txt"]);

        var hermetics = HermeticEnv(globalCfg);
        var (exitCode, _, stderr) = RunGitCaptureFullHermetic(
            repo.Root, ["commit", "-m", "chore: test"], hermetics);
        Assert.Equal(0, exitCode);

        // Must emit the warning.
        Assert.Contains("Visual Relay: removed repo-local user.name/user.email", stderr, StringComparison.Ordinal);

        // Local config must no longer contain user.name or user.email.
        var (exitName, stdoutName, _) = RunGitCaptureFullHermetic(
            repo.Root, ["config", "--local", "user.name"], hermetics);
        Assert.NotEqual(0, exitName); // git config exits non-zero when key is absent
        Assert.Equal("", stdoutName.Trim());
        var (exitEmail, stdoutEmail, _) = RunGitCaptureFullHermetic(
            repo.Root, ["config", "--local", "user.email"], hermetics);
        Assert.NotEqual(0, exitEmail);
        Assert.Equal("", stdoutEmail.Trim());
    }

    [Fact]
    public void StripIdentity_LagContract_FirstCommitKeepsOldIdentity_SecondCommitUsesGlobal()
    {
        using var globalCfg = WriteGlobalGitConfig("Global User", "global@example.test");
        using var repo = PreCommitHookTests.CreateRepoWithHook(configureLocalIdentity: false);

        // Inject local identity.
        PreCommitHookTests.RunGit(repo.Root, ["config", "--local", "user.name", "Evil User"]);
        PreCommitHookTests.RunGit(repo.Root, ["config", "--local", "user.email", "evil@example.test"]);

        var hermetics = HermeticEnv(globalCfg);

        // First commit: triggers strip, but carries the OLD local identity.
        File.WriteAllText(Path.Combine(repo.Root, "first.txt"), "first");
        PreCommitHookTests.RunGit(repo.Root, ["add", "first.txt"]);
        var (exit1, _, stderr1) = RunGitCaptureFullHermetic(
            repo.Root, ["commit", "-m", "chore: first"], hermetics);
        Assert.Equal(0, exit1);
        Assert.Contains("removed repo-local", stderr1, StringComparison.Ordinal);

        var (_, firstAuthor, _) = RunGitCaptureFullHermetic(
            repo.Root, ["log", "-1", "--format=%an <%ae>"], hermetics);
        Assert.Contains("Evil User <evil@example.test>", firstAuthor, StringComparison.Ordinal);

        // Second commit: local identity is already stripped, uses global.
        File.WriteAllText(Path.Combine(repo.Root, "second.txt"), "second");
        PreCommitHookTests.RunGit(repo.Root, ["add", "second.txt"]);
        var (exit2, _, stderr2) = RunGitCaptureFullHermetic(
            repo.Root, ["commit", "-m", "chore: second"], hermetics);
        Assert.Equal(0, exit2);
        // No warning on clean commit.
        Assert.DoesNotContain("removed repo-local", stderr2, StringComparison.Ordinal);

        var (_, secondAuthor, _) = RunGitCaptureFullHermetic(
            repo.Root, ["log", "-1", "--format=%an <%ae>"], hermetics);
        Assert.Contains("Global User <global@example.test>", secondAuthor, StringComparison.Ordinal);
    }

    [Fact]
    public void StripIdentity_CleanRepo_NoWarning()
    {
        using var globalCfg = WriteGlobalGitConfig("Global User", "global@example.test");
        using var repo = PreCommitHookTests.CreateRepoWithHook(configureLocalIdentity: false);

        var hermetics = HermeticEnv(globalCfg);

        File.WriteAllText(Path.Combine(repo.Root, "test.txt"), "hello");
        PreCommitHookTests.RunGit(repo.Root, ["add", "test.txt"]);
        var (exitCode, _, stderr) = RunGitCaptureFullHermetic(
            repo.Root, ["commit", "-m", "chore: test"], hermetics);
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("removed repo-local", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void StripIdentity_NoDefaultMachine_StrippedIdentityNeverAppears()
    {
        // Empty global config — simulates a machine with no global user.*.
        using var globalCfg = WriteGlobalGitConfig(); // no content
        using var repo = PreCommitHookTests.CreateRepoWithHook(configureLocalIdentity: false);

        // Inject local identity.
        PreCommitHookTests.RunGit(repo.Root, ["config", "--local", "user.name", "Evil User"]);
        PreCommitHookTests.RunGit(repo.Root, ["config", "--local", "user.email", "evil@example.test"]);

        var hermetics = HermeticEnv(globalCfg);

        // First commit triggers strip.
        File.WriteAllText(Path.Combine(repo.Root, "first.txt"), "first");
        PreCommitHookTests.RunGit(repo.Root, ["add", "first.txt"]);
        var (exit1, _, stderr1) = RunGitCaptureFullHermetic(
            repo.Root, ["commit", "-m", "chore: first"], hermetics);
        Assert.Equal(0, exit1);
        Assert.Contains("removed repo-local", stderr1, StringComparison.Ordinal);

        // Second commit: no local identity, no global identity.
        File.WriteAllText(Path.Combine(repo.Root, "second.txt"), "second");
        PreCommitHookTests.RunGit(repo.Root, ["add", "second.txt"]);
        var (exit2, stdout2, stderr2) = RunGitCaptureFullHermetic(
            repo.Root, ["commit", "-m", "chore: second"], hermetics);

        // On machines with a .local hostname, git auto-detects and commit succeeds.
        // On machines with a (none) hostname, git may refuse. Accept either.
        if (exit2 == 0)
        {
            // Commit succeeded — assert the stripped identity does NOT appear.
            var (_, secondAuthor, _) = RunGitCaptureFullHermetic(
                repo.Root, ["log", "-1", "--format=%an <%ae>"], hermetics);
            Assert.DoesNotContain("Evil User", secondAuthor, StringComparison.Ordinal);
            Assert.DoesNotContain("evil@example.test", secondAuthor, StringComparison.Ordinal);
        }
        else
        {
            // Commit was rejected — the error must NOT be about the stripped identity.
            var combined = stdout2 + stderr2;
            Assert.DoesNotContain("Evil User", combined, StringComparison.Ordinal);
            Assert.DoesNotContain("evil@example.test", combined, StringComparison.Ordinal);
        }
    }

    // ── private helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Like RunGitCapture but takes a dictionary for hermetic env vars and
    /// captures both stdout and stderr.
    /// </summary>
    private static (int ExitCode, string Stdout, string Stderr) RunGitCaptureFullHermetic(
        string rootPath,
        string[] arguments,
        IReadOnlyDictionary<string, string> env)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(rootPath);
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment.Remove("DEVELOPER_DIR");
        startInfo.Environment.Remove("SDKROOT");

        foreach (var (key, value) in env)
        {
            startInfo.EnvironmentVariables[key] = value;
        }

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Writes a temporary global git config file with the given user identity,
    /// returning a disposable that deletes the file on cleanup.
    /// </summary>
    private static TempFile WriteGlobalGitConfig(
        string name = "", string email = "")
    {
        var path = Path.Combine(Path.GetTempPath(),
            "vr-precommit-test-global-" + Guid.NewGuid().ToString("N") + ".gitconfig");
        if (name.Length > 0 && email.Length > 0)
        {
            File.WriteAllText(path, $"[user]\n\tname = {name}\n\temail = {email}\n");
        }
        else
        {
            File.WriteAllText(path, "");
        }

        return new TempFile(path);
    }

    /// <summary>Returns the hermetic env dictionary for identity-strip tests.</summary>
    private static Dictionary<string, string> HermeticEnv(TempFile globalCfg)
    {
        return new Dictionary<string, string>
        {
            ["GIT_CONFIG_GLOBAL"] = globalCfg.Path,
            ["GIT_CONFIG_SYSTEM"] = "/dev/null",
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
    }

    /// <summary>
    /// A temporary file that deletes itself on dispose.
    /// </summary>
    private sealed class TempFile(string path) : IDisposable
    {
        public string Path => path;

        public void Dispose()
        {
            try { File.Delete(Path); }
            catch { /* best-effort */ }
        }
    }
}
