using VisualRelay.Core.CommitLint;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests the IO-bearing orchestration of the commit-msg hook tool: gathering
/// the changed-file basenames + disallowed substrings, and — critically —
/// deciding the rule tier. The driver tier applies only when
/// <c>RELAY_COMMIT_TOKEN</c> matches the nonce in
/// <c>.relay/ACTIVE/info.json</c>, the same comparison <c>.githooks/pre-commit</c>
/// makes. All git/file IO lives here, not in the pure validator.
/// </summary>
public sealed class CommitLintRunnerTests
{
    private static void WriteActiveNonce(string root, string nonce)
    {
        var dir = Path.Combine(root, ".relay", "ACTIVE");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "info.json"), $"{{ \"nonce\": \"{nonce}\" }}");
    }

    [Fact]
    public async Task DecideTier_NoActiveRun_IsHuman()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: "anything", git, CancellationToken.None);
        Assert.Equal(RuleTier.Human, tier);
    }

    [Fact]
    public async Task DecideTier_ActiveRunTokenMatches_IsDriver()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        WriteActiveNonce(repo.Root, "abc123def456");

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: "abc123def456", git, CancellationToken.None);
        Assert.Equal(RuleTier.Driver, tier);
    }

    [Fact]
    public async Task DecideTier_ActiveRunTokenMismatch_IsHuman()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        WriteActiveNonce(repo.Root, "abc123def456");

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: "wrong-token", git, CancellationToken.None);
        Assert.Equal(RuleTier.Human, tier);
    }

    [Fact]
    public async Task DecideTier_ActiveRunNoToken_IsHuman()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        WriteActiveNonce(repo.Root, "abc123def456");

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: null, git, CancellationToken.None);
        Assert.Equal(RuleTier.Human, tier);
    }

    [Fact]
    public async Task GatherChangedBasenames_ReturnsStagedFileBasenames()
    {
        using var repo = ScratchRepo.Create();
        var git = new GitInvoker();
        await repo.InitAsync(git);
        // Seed a base commit so there is a HEAD, then stage a new file.
        await repo.SeedCommitAsync(git, "seed.txt", "x", "feat: seed\n",
            "2021-01-01T10:00:00", "2021-01-01T10:00:00");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "Widget.cs"), "// code");
        await git.RunAsync(repo.Root, ["add", "src/Widget.cs"], CancellationToken.None);

        var names = await CommitLintRunner.GatherChangedBasenamesAsync(repo.Root, git, CancellationToken.None);
        Assert.Contains("Widget.cs", names);
        Assert.DoesNotContain("src/Widget.cs", names);
    }

    [Fact]
    public void ReadDisallowed_AbsentFile_ReturnsEmpty()
    {
        using var repo = ScratchRepo.Create();
        var result = CommitLintRunner.ReadDisallowedSubstrings(repo.Root);
        Assert.Empty(result);
    }

    [Fact]
    public void ReadDisallowed_PresentFile_SkipsCommentsAndBlanks()
    {
        using var repo = ScratchRepo.Create();
        File.WriteAllText(Path.Combine(repo.Root, "disallowed-commit-messages.txt"),
            "# a comment\n\nwip\n  fixup  \n# another\ntemp-hack\n");
        var result = CommitLintRunner.ReadDisallowedSubstrings(repo.Root);
        Assert.Equal(["wip", "fixup", "temp-hack"], result);
    }

    [Fact]
    public void FormatViolations_MatchesReferenceStyle()
    {
        var violations = new List<Violation>
        {
            new("subject must not end with a period"),
            new("message must not contain an em dash (—, U+2014)"),
        };
        var text = CommitLintRunner.FormatViolations(violations);
        Assert.Contains("check-commit-message: 2 violation(s)", text);
        Assert.Contains("  - subject must not end with a period", text);
        Assert.Contains(CommitRules.RulesDoc, text);
    }
}
