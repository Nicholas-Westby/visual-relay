using VisualRelay.Core.CommitLint;
using VisualRelay.Core.Execution;
using GitSimEngine = VisualRelay.GitSim.GitSim;

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
        var (sim, repo) = GitSimTestHelpers.NewRepo();

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: "anything", sim, CancellationToken.None);
        Assert.Equal(RuleTier.Human, tier);
    }

    [Fact]
    public async Task DecideTier_ActiveRunTokenMatches_IsDriver()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        WriteActiveNonce(repo.Root, "abc123def456");

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: "abc123def456", sim, CancellationToken.None);
        Assert.Equal(RuleTier.Driver, tier);
    }

    [Fact]
    public async Task DecideTier_ActiveRunTokenMismatch_IsHuman()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        WriteActiveNonce(repo.Root, "abc123def456");

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: "wrong-token", sim, CancellationToken.None);
        Assert.Equal(RuleTier.Human, tier);
    }

    [Fact]
    public async Task DecideTier_ActiveRunNoToken_IsHuman()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        WriteActiveNonce(repo.Root, "abc123def456");

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: null, sim, CancellationToken.None);
        Assert.Equal(RuleTier.Human, tier);
    }

    private static void WriteRawActiveInfo(string root, string contents)
    {
        var dir = Path.Combine(root, ".relay", "ACTIVE");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "info.json"), contents);
    }

    [Fact]
    public async Task DecideTier_InfoMissingWithToken_NoThrowAndIsHuman()
    {
        // No active-run info.json at all: there is no run to be a driver for, so
        // even a stale/leftover token must NOT grant Driver. The read must not
        // throw; the tier is Human. (Fail-open to Driver is only for a PRESENT
        // info.json that cannot be read/parsed mid start/stop.)
        var (sim, repo) = GitSimTestHelpers.NewRepo();

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: "tok", sim, CancellationToken.None);
        Assert.Equal(RuleTier.Human, tier);
    }

    [Fact]
    public async Task DecideTier_MalformedJsonWithToken_DoesNotThrowAndIsDriver()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        // Truncated mid-write — exactly the start/stop race this guards against.
        WriteRawActiveInfo(repo.Root, "{ \"nonce\": \"abc12");

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: "abc123def456", sim, CancellationToken.None);
        Assert.Equal(RuleTier.Driver, tier);
    }

    [Fact]
    public async Task DecideTier_MalformedJsonNoToken_DoesNotThrowAndIsHuman()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        WriteRawActiveInfo(repo.Root, "not json at all {{{");

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: null, sim, CancellationToken.None);
        Assert.Equal(RuleTier.Human, tier);
    }

    [Fact]
    public async Task DecideTier_GarbledInfoWithToken_FailsOpenToDriver()
    {
        // A present but unreadable-as-nonce info.json with a token set must fail
        // open to Driver, never propagate and abort the commit.
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        WriteRawActiveInfo(repo.Root, "\0\0\0 binary garbage \xff");

        var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: "some-token", sim, CancellationToken.None);
        Assert.Equal(RuleTier.Driver, tier);
    }

    [Fact]
    public async Task DecideTier_UnreadableInfoWithToken_DoesNotThrowAndIsDriver()
    {
        // A genuine read failure on a PRESENT info.json must be caught and fail
        // open to Driver, never propagate to abort the commit. Force the failure
        // cross-platform by holding an exclusive (FileShare.None) handle, so the
        // hook's File.ReadAllText throws IOException.
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        var dir = Path.Combine(repo.Root, ".relay", "ACTIVE");
        Directory.CreateDirectory(dir);
        var info = Path.Combine(dir, "info.json");
        File.WriteAllText(info, "{ \"nonce\": \"abc123def456\" }");

        await using (new FileStream(info, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var tier = await CommitLintRunner.DecideTierAsync(repo.Root, token: "abc123def456", sim, CancellationToken.None);
            Assert.Equal(RuleTier.Driver, tier);
        }
    }

    [Fact]
    public async Task GatherChangedBasenames_ReturnsStagedFileBasenames()
    {
        var (sim, repo) = GitSimTestHelpers.NewRepo();
        // Seed a base commit so there is a HEAD, then stage a new file.
        sim.Seed(repo.Root, "seed.txt", "x");
        sim.Commit(repo.Root, "feat: seed");
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "Widget.cs"), "// code");
        await sim.RunAsync(repo.Root, ["add", "src/Widget.cs"], CancellationToken.None);

        var names = await CommitLintRunner.GatherChangedBasenamesAsync(repo.Root, sim, CancellationToken.None);
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
