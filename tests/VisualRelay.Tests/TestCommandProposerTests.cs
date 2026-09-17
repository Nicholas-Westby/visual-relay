using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Every repository that defeated bootstrap's marker table so far was answered with
/// another per-language rule, which goes out of date and helps one ecosystem. The
/// proposer is the general fallback instead: an agent run with the normal tools, so it
/// can read the CI configuration, the README and the scripts they point at — evidence
/// that lives inside files, which a list of file NAMES cannot carry.
/// </summary>
public sealed class TestCommandProposerTests
{
    private static RelayConfig Config() =>
        RelayConfigLoader.Defaults(ProjectBootstrapper.PlaceholderTestCommand);

    private sealed class ScriptedRunner(string? json) : ISubagentRunner
    {
        public StageInvocation? Seen { get; private set; }

        public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
        {
            Seen = invocation;
            return Task.FromResult(json is null
                ? new SubagentResult(string.Empty, null, false, "no answer")
                : new SubagentResult(json, json, true, null));
        }
    }

    [Fact]
    public async Task RunAsync_AsksOnTheCheapTierWithTheContractAndTheRootAsItsTarget()
    {
        using var repo = TestRepository.Create();
        var runner = new ScriptedRunner("""{"testCmd":"go test ./...","evidence":"the CI workflow runs it"}""");

        var proposed = await TestCommandProposer.RunAsync(repo.Root, [], Config(), runner, CancellationToken.None);

        Assert.Equal("go test ./...", proposed);
        var seen = runner.Seen!;
        Assert.Equal(0, seen.Stage.Number);
        Assert.Equal("TestCommandProposer", seen.Stage.Name);
        Assert.Equal("cheap", seen.Tier);
        Assert.Equal(TestCommandProposer.OutputContract, seen.Stage.OutputContract);
        Assert.Equal(repo.Root, seen.TargetRoot);
        Assert.Equal(TestCommandProposer.MaxTurns, seen.MaxTurns);
        Assert.Equal(Path.Combine(repo.Root, ".relay", "bootstrap"), seen.TraceDirectory);
    }

    /// <summary>
    /// The first rejection's own output usually names the thing in the way — a project
    /// that has to be excluded, a runner that found no tests — so the head of it is
    /// worth more to the proposer than the reason alone.
    /// </summary>
    [Fact]
    public async Task RunAsync_TheInputRendersEachAttemptWithItsExitCodeAndOutputHead()
    {
        using var repo = TestRepository.Create();
        var runner = new ScriptedRunner("""{"testCmd":"x","evidence":"y"}""");
        CommandAttempt[] attempts =
        [
            new("dotnet test Ocelot.slnx", 1, "All projects must use that test runner ... Ocelot.Benchmarks.csproj"),
            new("dotnet test Ocelot.Samples.slnx", 1, "No test projects were found"),
        ];

        await TestCommandProposer.RunAsync(repo.Root, attempts, Config(), runner, CancellationToken.None);

        var input = runner.Seen!.TaskInput;
        Assert.Contains("dotnet test Ocelot.slnx", input, StringComparison.Ordinal);
        Assert.Contains("(exit 1)", input, StringComparison.Ordinal);
        Assert.Contains("Ocelot.Benchmarks.csproj", input, StringComparison.Ordinal);
        Assert.Contains("No test projects were found", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildInput_WithNoAttempts_SaysTheTableRecognisedNothing()
    {
        await Task.CompletedTask;
        Assert.Contains("recognised no build or package file", TestCommandProposer.BuildInput([]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_OnlyTheOutputHeadIsSent()
    {
        using var repo = TestRepository.Create();
        var runner = new ScriptedRunner("""{"testCmd":"x","evidence":"y"}""");
        var output = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}"));

        await TestCommandProposer.RunAsync(
            repo.Root, [new CommandAttempt("cmd", 1, output)], Config(), runner, CancellationToken.None);

        Assert.Contains("line 30", runner.Seen!.TaskInput, StringComparison.Ordinal);
        Assert.DoesNotContain("line 31", runner.Seen.TaskInput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"evidence":"no command here"}""")]
    [InlineData("""{"testCmd":"   "}""")]
    [InlineData("not json at all")]
    public async Task RunAsync_AnUnusableAnswer_ReturnsNull(string? json)
    {
        using var repo = TestRepository.Create();

        var proposed = await TestCommandProposer.RunAsync(
            repo.Root, [], Config(), new ScriptedRunner(json), CancellationToken.None);

        Assert.Null(proposed);
    }

    /// <summary>A bootstrap that cannot ask falls back to the placeholder; it never fails.</summary>
    [Fact]
    public async Task RunAsync_WhenTheRunnerThrows_ReturnsNull()
    {
        using var repo = TestRepository.Create();

        var proposed = await TestCommandProposer.RunAsync(
            repo.Root, [], Config(), new ThrowingRunner(), CancellationToken.None);

        Assert.Null(proposed);
    }

    private sealed class ThrowingRunner : ISubagentRunner
    {
        public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no provider key");
    }
}
