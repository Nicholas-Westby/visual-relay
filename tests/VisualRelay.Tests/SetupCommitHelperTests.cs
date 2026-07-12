using VisualRelay.Core.Init;
using VisualRelay.GitSim;

namespace VisualRelay.Tests;

public sealed class SetupCommitHelperTests
{
    [Fact]
    public async Task TryCommitSetupFiles_FreshBootstrap_CommitsBothFiles()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        // Simulate what GitBootstrapper does: an empty root commit so HEAD resolves.
        sim.Commit(repo.Root, "chore: initialize repository (visual-relay bootstrap)");

        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        GitSimTestHelpers.Write(repo, ".relay/config.json", "{\"testCmd\":\"dotnet test\"}");
        GitSimTestHelpers.Write(repo, ".relay/.gitignore", RelayGitignoreWriter.Content);

        var diagnostic = await SetupCommitHelper.TryCommitSetupFilesAsync(repo.Root, sim);

        Assert.Null(diagnostic);

        // The commit with the setup message exists and touches exactly the two files.
        var head = sim.Head(repo.Root);
        Assert.NotNull(head);
        var info = sim.CommitInfo(repo.Root, head!);
        Assert.NotNull(info);
        Assert.Equal("chore(relay): initialize project config", info!.Message);

        var changedFiles = sim.FilesChangedInCommit(repo.Root, head!);
        Assert.Equal(2, changedFiles.Count);
        Assert.Contains(".relay/config.json", changedFiles);
        Assert.Contains(".relay/.gitignore", changedFiles);

        // No .relay/ entries in git status (tree is clean).
        var (_, statusOut) = await sim.Git(repo.Root, "status", "--porcelain");
        Assert.DoesNotContain(".relay/", statusOut);
    }

    [Fact]
    public async Task TryCommitSetupFiles_SecondBootstrap_Idempotent()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        // Simulate what GitBootstrapper does: an empty root commit so HEAD resolves.
        sim.Commit(repo.Root, "chore: initialize repository (visual-relay bootstrap)");

        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        GitSimTestHelpers.Write(repo, ".relay/config.json", "{\"testCmd\":\"dotnet test\"}");
        GitSimTestHelpers.Write(repo, ".relay/.gitignore", RelayGitignoreWriter.Content);

        // First call creates the commit.
        var diag1 = await SetupCommitHelper.TryCommitSetupFilesAsync(repo.Root, sim);
        Assert.Null(diag1);

        // Second call must not create a second commit.
        var diag2 = await SetupCommitHelper.TryCommitSetupFilesAsync(repo.Root, sim);
        Assert.Null(diag2);

        // Only one commit with the setup message exists.
        // Walk the log and count commits with the setup message.
        var head = sim.Head(repo.Root)!;
        var commitCount = 0;
        var sha = head;
        while (sha is not null)
        {
            var info = sim.CommitInfo(repo.Root, sha);
            if (info is not null && info.Message == "chore(relay): initialize project config")
                commitCount++;
            sha = info?.Parents.FirstOrDefault();
        }

        Assert.Equal(1, commitCount);
    }

    [Fact]
    public async Task TryCommitSetupFiles_UserStagedFile_NotSweptIn()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        // Simulate what GitBootstrapper does: an empty root commit so HEAD resolves.
        sim.Commit(repo.Root, "chore: initialize repository (visual-relay bootstrap)");

        // Stage an unrelated file first.
        GitSimTestHelpers.Write(repo, "src/app.cs", "// user code");
        await sim.Git(repo.Root, "add", "src/app.cs");

        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        GitSimTestHelpers.Write(repo, ".relay/config.json", "{\"testCmd\":\"dotnet test\"}");
        GitSimTestHelpers.Write(repo, ".relay/.gitignore", RelayGitignoreWriter.Content);

        var diagnostic = await SetupCommitHelper.TryCommitSetupFilesAsync(repo.Root, sim);

        Assert.Null(diagnostic);

        // The setup commit contains only the two .relay files.
        var head = sim.Head(repo.Root)!;
        var changedFiles = sim.FilesChangedInCommit(repo.Root, head);
        Assert.Equal(2, changedFiles.Count);
        Assert.Contains(".relay/config.json", changedFiles);
        Assert.Contains(".relay/.gitignore", changedFiles);
        Assert.DoesNotContain("src/app.cs", changedFiles);

        // The user's staged file remains staged after the setup commit.
        var staged = sim.StagedPaths(repo.Root);
        Assert.Contains("src/app.cs", staged);
    }

    [Fact]
    public async Task TryCommitSetupFiles_StrictHook_Succeeds()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        // Simulate what GitBootstrapper does: an empty root commit so HEAD resolves.
        sim.Commit(repo.Root, "chore: initialize repository (visual-relay bootstrap)");

        // Install a pre-commit hook that rejects when untracked files (others) exist.
        sim.PreCommitHook = req =>
        {
            // Simulate `git ls-files --others --exclude-standard`:
            // check if any non-ignored, non-tracked files exist on disk.
            var trackedAndStaged = new HashSet<string>(req.StagedPaths, StringComparer.Ordinal);
            var allFiles = Directory.EnumerateFiles(repo.Root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(repo.Root, f).Replace('\\', '/'))
                .Where(f => !f.StartsWith(".git/", StringComparison.Ordinal))
                .ToList();
            var untracked = allFiles
                .Where(f => !trackedAndStaged.Contains(f) && !sim.IsIgnored(repo.Root, f))
                .ToList();
            if (untracked.Count > 0)
                return GitSimHookVerdict.Reject(
                    $"error: untracked files present: {string.Join(", ", untracked)}");
            return GitSimHookVerdict.Accept;
        };

        // First, ensure the two setup files are COMMITTED (so they become tracked).
        // The setup commit itself stages and commits them; at hook time
        // during the setup commit, the files are staged → no untracked residue.
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        GitSimTestHelpers.Write(repo, ".relay/config.json", "{\"testCmd\":\"dotnet test\"}");
        GitSimTestHelpers.Write(repo, ".relay/.gitignore", RelayGitignoreWriter.Content);

        var diagnostic = await SetupCommitHelper.TryCommitSetupFilesAsync(repo.Root, sim);
        Assert.Null(diagnostic); // Setup commit succeeds — files are staged at hook time.

        // Now stage and commit an unrelated file — must succeed (no .relay residue).
        GitSimTestHelpers.Write(repo, "src/app.cs", "// new code");
        await sim.Git(repo.Root, "add", "src/app.cs");
        var (commitExit, commitOut) = await sim.Git(repo.Root, "commit", "-m", "feat: add app");
        Assert.Equal(0, commitExit);
        Assert.Contains("feat: add app", commitOut);
    }

    [Fact]
    public async Task TryCommitSetupFiles_HookAlwaysRejects_ReturnsDiagnostic()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        // Simulate what GitBootstrapper does: an empty root commit so HEAD resolves.
        sim.Commit(repo.Root, "chore: initialize repository (visual-relay bootstrap)");

        // Install a pre-commit hook that always rejects.
        sim.PreCommitHook = _ =>
            GitSimHookVerdict.Reject("error: pre-commit hook rejected — custom policy");

        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        GitSimTestHelpers.Write(repo, ".relay/config.json", "{\"testCmd\":\"dotnet test\"}");
        GitSimTestHelpers.Write(repo, ".relay/.gitignore", RelayGitignoreWriter.Content);

        var diagnostic = await SetupCommitHelper.TryCommitSetupFilesAsync(repo.Root, sim);

        // Must return a diagnostic — not null (the commit was rejected).
        Assert.NotNull(diagnostic);
        Assert.Contains("pre-commit hook rejected", diagnostic!.OutputTail);
        Assert.NotEqual(0, diagnostic.ExitCode);

        // Files must remain on disk.
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", ".gitignore")));

        // The setup-check.log artifact must exist on disk.
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "setup-check.log")));

        // No setup commit was created — HEAD still points to the root commit only.
        var head = sim.Head(repo.Root);
        Assert.NotNull(head);
        var info = sim.CommitInfo(repo.Root, head!);
        Assert.NotNull(info);
        Assert.Equal("chore: initialize repository (visual-relay bootstrap)", info!.Message);
    }

    [Fact]
    public void EnsureGitignore_DriverRunPrep_WritesButDoesNotCommit()
    {
        using var repo = TestRepository.Create();
        // Simulate hand-written .relay/config.json without .relay/.gitignore.
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        File.WriteAllText(Path.Combine(repo.Root, ".relay", "config.json"),
            "{\"testCmd\":\"dotnet test\"}");

        // Call EnsureWritten directly (the driver prep belt-and-suspenders).
        var written = RelayGitignoreWriter.EnsureWritten(repo.Root);

        Assert.True(written);
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", ".gitignore")));
        Assert.Equal(RelayGitignoreWriter.Content,
            File.ReadAllText(Path.Combine(repo.Root, ".relay", ".gitignore")));

        // Verify no commit was created — EnsureWritten does not touch git.
        // (No .git directory exists at all in this scenario, so it can't have committed.)
        Assert.False(Directory.Exists(Path.Combine(repo.Root, ".git")));
    }

    [Fact]
    public async Task TryCommitSetupFiles_NonGitRepo_SkipsSilently()
    {
        using var repo = TestRepository.Create();
        // No git repo at all — simulates CreateConfigAsync on a non-git folder.
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        File.WriteAllText(Path.Combine(repo.Root, ".relay", "config.json"),
            "{\"testCmd\":\"dotnet test\"}");
        File.WriteAllText(Path.Combine(repo.Root, ".relay", ".gitignore"),
            RelayGitignoreWriter.Content);

        var diagnostic = await SetupCommitHelper.TryCommitSetupFilesAsync(repo.Root);

        // Must return null — no false diagnostic, no crash.
        Assert.Null(diagnostic);

        // Files still on disk (init completed, we just couldn't commit).
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", "config.json")));
        Assert.True(File.Exists(Path.Combine(repo.Root, ".relay", ".gitignore")));

        // No bogus setup-check.log artifact written.
        Assert.False(File.Exists(Path.Combine(repo.Root, ".relay", "setup-check.log")));
    }
}
