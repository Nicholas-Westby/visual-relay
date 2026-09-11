using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// The pure half of the Author-tests diff audit: when it runs at all, what the
/// one model call is asked, and what it is shown. Nothing here reaches a model.
/// </summary>
public sealed class AuthorTestDiffAuditorTests
{
    /// <summary>Language and framework names no prompt of ours may name.</summary>
    public static IEnumerable<object[]> LanguageNames =>
        new[]
        {
            "rust", "cargo", ".rs", "python", "pytest", ".py", "golang", "javascript",
            "typescript", "vitest", "jest", "java", "junit", "swift", "xctest", "ruby",
            "rspec", "dotnet", "xunit", "elixir", "erlang", "kotlin", "haskell"
        }.Select(name => new object[] { name });

    [Theory]
    // off: never, whatever the stage declared.
    [InlineData(AuthorTestsConfig.DiffAuditOff, "", false)]
    [InlineData(AuthorTestsConfig.DiffAuditOff, "separate", false)]
    [InlineData(AuthorTestsConfig.DiffAuditOff, "inline", false)]
    [InlineData(AuthorTestsConfig.DiffAuditOff, "suspect", false)]
    // always: every declared list, and only an empty one has nothing to audit.
    [InlineData(AuthorTestsConfig.DiffAuditAlways, "", false)]
    [InlineData(AuthorTestsConfig.DiffAuditAlways, "separate", true)]
    [InlineData(AuthorTestsConfig.DiffAuditAlways, "inline", true)]
    [InlineData(AuthorTestsConfig.DiffAuditAlways, "suspect", true)]
    // auto: only where the path gate cannot decide on its own.
    [InlineData(AuthorTestsConfig.DiffAuditAuto, "", false)]
    [InlineData(AuthorTestsConfig.DiffAuditAuto, "separate", false)]
    [InlineData(AuthorTestsConfig.DiffAuditAuto, "separate,separate", false)]
    [InlineData(AuthorTestsConfig.DiffAuditAuto, "inline", true)]
    [InlineData(AuthorTestsConfig.DiffAuditAuto, "suspect", true)]
    [InlineData(AuthorTestsConfig.DiffAuditAuto, "separate,inline", true)]
    [InlineData(AuthorTestsConfig.DiffAuditAuto, "separate,suspect", true)]
    public void ShouldRun_FollowsTheConfiguredMode(string mode, string kinds, bool expected) =>
        Assert.Equal(expected, AuthorTestDiffAuditor.ShouldRun(mode, Verdicts(kinds)));

    /// <summary>An unreadable mode is the loader's fallback, which is <c>auto</c>.</summary>
    [Fact]
    public void ShouldRun_UnknownMode_BehavesLikeAuto()
    {
        Assert.False(AuthorTestDiffAuditor.ShouldRun("sometimes", Verdicts("separate")));
        Assert.True(AuthorTestDiffAuditor.ShouldRun("sometimes", Verdicts("inline")));
    }

    [Fact]
    public void BuildPrompt_AsksForTheContractAndShowsTheDiff()
    {
        var prompt = AuthorTestDiffAuditor.BuildPrompt("+++ b/src/control\n+fn normalize() {}\n");

        Assert.Contains("implementationHunks", prompt, StringComparison.Ordinal);
        Assert.Contains("\"file\"", prompt, StringComparison.Ordinal);
        Assert.Contains("\"reason\"", prompt, StringComparison.Ordinal);
        Assert.Contains("+fn normalize() {}", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// The audit is the one place a wrong language guess would be invisible, so
    /// the prompt describes hunks by what they do and names no language at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(LanguageNames))]
    public void BuildPrompt_NamesNoLanguage(string name) =>
        Assert.DoesNotContain(name, AuthorTestDiffAuditor.BuildPrompt("diff"), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task BuildDiff_AddsUntrackedTestFilesWholeAndCapsTheWhole()
    {
        using var repo = TestRepository.Create();
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app", "old\n");
        sim.Commit(repo.Root, "seed");
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, "tests", "big"), new string('x', 80_000));

        var diff = await AuthorTestDiffAuditor.BuildDiffAsync(
            repo.Root, ["tests/big"], sim, TestContext.Current.CancellationToken);

        Assert.Contains("tests/big", diff, StringComparison.Ordinal);
        Assert.Contains("truncated", diff, StringComparison.OrdinalIgnoreCase);
        Assert.True(diff.Length <= 60_100, $"diff was {diff.Length} characters");
    }

    [Fact]
    public async Task BuildDiff_SkipsAPathThatEscapesTheWorkspace()
    {
        using var repo = TestRepository.Create();
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app", "old\n");
        sim.Commit(repo.Root, "seed");

        var diff = await AuthorTestDiffAuditor.BuildDiffAsync(
            repo.Root, ["../outside/secret"], sim, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("secret", diff, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one call is answered from the prompt alone, so it is sent with no tools:
    /// on the live default model the audit spent its single turn on two tool calls
    /// and then reported an exhausted turn budget, every time it fired.
    /// </summary>
    [Fact]
    public async Task RunAsync_AsksForOneToollessTurnAgainstATopLevelContract()
    {
        using var repo = TestRepository.Create();
        var sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, "src/app", "old\n");
        sim.Commit(repo.Root, "seed");
        Directory.CreateDirectory(Path.Combine(repo.Root, "tests"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "tests", "t"), "asserts\n");
        var runner = new CapturingSubagentRunner("""{"implementationHunks":[]}""");

        var result = await AuthorTestDiffAuditor.RunAsync(
            repo.Root, "a-task", "run-1", ["tests/t"], RelayConfigLoader.Defaults(),
            runner, sim, new InMemoryRelayEventSink(), TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Empty(result.ImplementationHunks);
        var invocation = Assert.Single(runner.Invocations);
        Assert.True(invocation.WithoutTools, "the audit must be sent with no tools");
        Assert.Equal(1, invocation.MaxTurns);
        // The reader requires every key the contract names, so naming the element's
        // keys there makes an empty answer impossible to satisfy.
        Assert.DoesNotContain("\"file\"", invocation.Stage.OutputContract, StringComparison.Ordinal);
        Assert.Contains("implementationHunks", invocation.Stage.OutputContract, StringComparison.Ordinal);
    }

    /// <summary>Records what the audit asked for and answers with canned JSON.</summary>
    private sealed class CapturingSubagentRunner(string json) : ISubagentRunner
    {
        public List<StageInvocation> Invocations { get; } = [];

        public Task<SubagentResult> RunAsync(
            StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            Invocations.Add(invocation);
            return Task.FromResult(new SubagentResult(json, json, true, null));
        }
    }

    private static IReadOnlyList<AuthorTestScopeVerdict> Verdicts(string kinds) =>
        kinds.Length == 0
            ? []
            : [.. kinds.Split(',').Select((kind, index) => new AuthorTestScopeVerdict($"f{index}", Kind(kind)))];

    private static AuthorTestScopeKind Kind(string kind) => kind switch
    {
        "separate" => AuthorTestScopeKind.Separate,
        "inline" => AuthorTestScopeKind.InlineCapable,
        _ => AuthorTestScopeKind.Suspect,
    };
}
