using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;
using GitSimEngine = VisualRelay.GitSim.GitSim;

namespace VisualRelay.Tests;

/// <summary>
/// When none of bootstrap's own candidates passes, it asks a model rather than growing
/// the marker table by one more per-language rule. What makes the answer safe to keep
/// is the check a built-in candidate already has to pass, and the proposal goes through
/// exactly that.
/// </summary>
public sealed class ProjectBootstrapperProposalTests
{
    private static (TestRepository Repo, GitSimEngine Sim) GoRepo()
    {
        var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        sim.InitRepo(repo.Root);
        sim.Seed(repo.Root, "go.mod", "module example.com/m\n\ngo 1.22\n");
        sim.Seed(repo.Root, "main.go", "package main\n");
        sim.Commit(repo.Root, "seed");
        return (repo, sim);
    }

    /// <summary>Records what it was asked to run, so which runner saw the proposal is assertable.</summary>
    private sealed class Recorder(params TestRunResult[] results) : ITestRunner
    {
        private int _index;

        public List<string> Commands { get; } = [];

        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            var result = _index < results.Length ? results[_index] : results[^1];
            _index++;
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task AllCandidatesRejected_TheFirstAcceptedProposalIsWritten()
    {
        var (repo, sim) = GoRepo();
        using var _ = repo;
        var asked = new List<IReadOnlyList<CommandAttempt>>();

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root,
            gitInvoker: sim,
            validationRunner: new Recorder(new TestRunResult(127, "sh: go: command not found")),
            proposeCommand: (attempts, _) =>
            {
                asked.Add(attempts);
                return Task.FromResult<string?>("go test ./pkg/...");
            },
            proposalRunner: _ => new Recorder(new TestRunResult(0, "ok  example.com/m  0.002s")));

        Assert.Equal("go test ./pkg/...", result.TestCommand);
        Assert.False(result.UsedPlaceholderTestCommand);
        Assert.Equal(TestCommandSource.Proposed, result.TestCommandSource);

        // The proposer was handed the rejected command AND its output, not just a reason.
        var handed = Assert.Single(asked);
        var attempt = Assert.Single(handed);
        Assert.Contains("go test", attempt.Command, StringComparison.Ordinal);
        Assert.Equal(127, attempt.ExitCode);
        Assert.Contains("command not found", attempt.Output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The proposal is checked under the SANDBOX the pipeline will run it through
    /// anyway, not the bare runner the built-in candidates use. It is a command written
    /// by a model that has been reading an unfamiliar repository.
    /// </summary>
    [Fact]
    public async Task TheProposal_IsCheckedUnderTheSandboxedRunner_NeverTheBareOne()
    {
        var (repo, sim) = GoRepo();
        using var _ = repo;
        var bare = new Recorder(new TestRunResult(127, "sh: go: command not found"));
        var sandboxed = new Recorder(new TestRunResult(0, "ok  example.com/m  0.002s"));

        await ProjectBootstrapper.BootstrapAsync(
            repo.Root,
            gitInvoker: sim,
            validationRunner: bare,
            proposeCommand: (_, _) => Task.FromResult<string?>("go test ./pkg/..."),
            proposalRunner: _ => sandboxed);

        // The bare runner saw the built-in candidate and the formatter check, and never
        // the proposal; the proposal went to the other one.
        Assert.DoesNotContain(bare.Commands, c => c.Contains("./pkg/...", StringComparison.Ordinal));
        Assert.Contains(sandboxed.Commands, c => c.Contains("./pkg/...", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoRejectedProposals_GiveThePlaceholderAndListEverythingTried()
    {
        var (repo, sim) = GoRepo();
        using var _ = repo;
        var proposals = new Queue<string>(["first --wrong", "second --wrong"]);

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root,
            gitInvoker: sim,
            validationRunner: new Recorder(new TestRunResult(127, "sh: go: command not found")),
            proposeCommand: (_, _) => Task.FromResult<string?>(proposals.Count > 0 ? proposals.Dequeue() : null),
            proposalRunner: _ => new Recorder(new TestRunResult(127, "sh: first: command not found")));

        Assert.True(result.UsedPlaceholderTestCommand);
        Assert.Equal(TestCommandSource.Placeholder, result.TestCommandSource);
        var tried = result.SetupCheck!.Rejections.Select(r => r.Candidate).ToList();
        Assert.Contains(tried, c => c.Contains("go test", StringComparison.Ordinal));
        Assert.Contains("first --wrong", tried);
        Assert.Contains("second --wrong", tried);
    }

    /// <summary>Asking again for the same answer spends another agent run to learn nothing.</summary>
    [Fact]
    public async Task ARepeatedProposal_EndsTheFallbackAfterOneValidation()
    {
        var (repo, sim) = GoRepo();
        using var _ = repo;
        var asks = 0;

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root,
            gitInvoker: sim,
            validationRunner: new Recorder(new TestRunResult(127, "sh: go: command not found")),
            proposeCommand: (_, _) =>
            {
                asks++;
                return Task.FromResult<string?>("always the same");
            },
            proposalRunner: _ => new Recorder(new TestRunResult(127, "sh: always: command not found")));

        Assert.Equal(2, asks);
        Assert.True(result.UsedPlaceholderTestCommand);
        Assert.Single(result.SetupCheck!.Rejections, r => r.Candidate == "always the same");
    }

    /// <summary>
    /// A folder with no tracked source files is greenfield: there is nothing to find,
    /// so the proposer is never started and the scaffolding advice stands.
    /// </summary>
    [Fact]
    public async Task AGreenfieldFolder_NeverCallsTheProposer()
    {
        using var repo = TestRepository.Create();
        var sim = new GitSimEngine();
        var called = false;

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root,
            gitInvoker: sim,
            proposeCommand: (_, _) =>
            {
                called = true;
                return Task.FromResult<string?>("anything");
            });

        Assert.False(called);
        Assert.True(result.UsedPlaceholderTestCommand);
    }

    [Fact]
    public async Task ANullProposer_LeavesTodaysResult()
    {
        var (repo, sim) = GoRepo();
        using var _ = repo;

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root, gitInvoker: sim, validationRunner: new Recorder(new TestRunResult(127, "sh: go: command not found")));

        Assert.True(result.UsedPlaceholderTestCommand);
        Assert.Equal(TestCommandSource.Placeholder, result.TestCommandSource);
    }

    /// <summary>
    /// The headline used to give scaffolding advice whatever happened. On a repository
    /// that HAS source files that was misleading: the honest answer is that nothing
    /// passed, and here is what was tried.
    /// </summary>
    [Fact]
    public async Task TheStatus_NamesWhatWasTried_AndDropsTheScaffoldingAdvice()
    {
        var (repo, sim) = GoRepo();
        using var _ = repo;

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root, gitInvoker: sim, validationRunner: new Recorder(new TestRunResult(127, "sh: go: command not found")));

        var text = VisualRelay.App.ViewModels.MainWindowViewModel.DescribeBootstrap(result);
        Assert.Contains("No test command passed the check", text, StringComparison.Ordinal);
        Assert.Contains("Set testCmd in .relay/config.json", text, StringComparison.Ordinal);
        Assert.DoesNotContain("scaffolds the project", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStatus_ForAProposedCommand_SaysItWasProposedAndChecked()
    {
        var (repo, sim) = GoRepo();
        using var _ = repo;

        var result = await ProjectBootstrapper.BootstrapAsync(
            repo.Root,
            gitInvoker: sim,
            validationRunner: new Recorder(new TestRunResult(127, "sh: go: command not found")),
            proposeCommand: (_, _) => Task.FromResult<string?>("go test ./pkg/..."),
            proposalRunner: _ => new Recorder(new TestRunResult(0, "ok  example.com/m  0.002s")));

        var text = VisualRelay.App.ViewModels.MainWindowViewModel.DescribeBootstrap(result);
        Assert.Contains("proposed by the model and checked", text, StringComparison.Ordinal);
    }

    /// <summary>A greenfield folder keeps the advice, because there the advice is right.</summary>
    [Fact]
    public async Task AGreenfieldResult_KeepsTheScaffoldingSentence()
    {
        using var repo = TestRepository.Create();

        var result = await ProjectBootstrapper.BootstrapAsync(repo.Root, gitInvoker: new GitSimEngine());

        var text = VisualRelay.App.ViewModels.MainWindowViewModel.DescribeBootstrap(result);
        Assert.Contains("scaffolds the project", text, StringComparison.Ordinal);
    }
}
