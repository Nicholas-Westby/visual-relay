using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Every run in a snapshot of the checkout imports the snapshot's own editable Python project.
/// Measured on openai-agents-python: the verify snapshot imported the checkout's src through the
/// overlaid .venv, and 35 tests that check traceback frames lie under their own src/agents failed
/// there, which cost a Fix-verify stage. The pristine base checkout the guard attribution probe
/// runs in had the same leak, so it judged the guard on the changed code.
/// </summary>
public sealed class RelayDriverSnapshotPythonImportsTests
{
    private const string GuardCmd = "tools/guards/check.sh";

    [Fact]
    public async Task TheVerifySnapshot_ImportsTheEditableProjectFromItself()
    {
        using var repo = SeededRepo(out var sim, withVirtualenv: true);
        repo.WriteConfig("pytest", [], baselineVerify: false);
        repo.WriteTask("py-task", "# A task\n");
        var tests = new SearchPathRecordingTestRunner(repo.Root, guardRedInCheckoutFirst: false);
        var events = new InMemoryRelayEventSink();

        var outcome = await RunAsync(repo, sim, tests, events, "py-task");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var verify = Assert.Single(tests.Calls, c => c.RootPath != repo.Root);
        Assert.Equal(Path.Combine(verify.RootPath, "src"), verify.SearchPaths?["PYTHONPATH"]);
        Assert.All(tests.Calls.Where(c => c.RootPath == repo.Root), c => Assert.Null(c.SearchPaths));
        var redirect = Assert.Single(events.Events, e => e.EventName == "snapshot_python_imports");
        Assert.Equal(Path.Combine(verify.RootPath, "src"), redirect.Data?["pythonPath"]);
    }

    [Fact]
    public async Task TheVerifySnapshotOfAProjectWithoutAVirtualenv_RunsAsBefore()
    {
        using var repo = SeededRepo(out var sim, withVirtualenv: false);
        repo.WriteConfig("pytest", [], baselineVerify: false);
        repo.WriteTask("plain-task", "# A task\n");
        var tests = new SearchPathRecordingTestRunner(repo.Root, guardRedInCheckoutFirst: false);
        var events = new InMemoryRelayEventSink();

        await RunAsync(repo, sim, tests, events, "plain-task");

        Assert.All(tests.Calls, c => Assert.Null(c.SearchPaths));
        Assert.DoesNotContain(events.Events, e => e.EventName == "snapshot_python_imports");
    }

    [Fact]
    public async Task TheGuardAttributionBaseCheckout_ImportsTheEditableProjectFromItself()
    {
        using var repo = SeededRepo(out var sim, withVirtualenv: true);
        repo.WriteConfig("pytest", [], baselineVerify: false, enableFixVerify: true, guardCmd: GuardCmd);
        repo.WriteTask("guard-task", "# A task\n");
        var tests = new SearchPathRecordingTestRunner(repo.Root, guardRedInCheckoutFirst: true);

        var outcome = await RunAsync(repo, sim, tests, new InMemoryRelayEventSink(), "guard-task");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var probe = Assert.Single(tests.Calls, c => c.Command == GuardCmd && c.RootPath != repo.Root);
        Assert.Equal(Path.Combine(probe.RootPath, "src"), probe.SearchPaths?["PYTHONPATH"]);
    }

    private static async Task<RelayTaskOutcome> RunAsync(
        TestRepository repo, VisualRelay.GitSim.GitSim sim, ITestRunner tests, InMemoryRelayEventSink events, string taskId)
    {
        var agent = new CapturingSubagentRunner();
        agent.SeedHappyPath("src/pkg/__init__.py", "tests/test_pkg.py");
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(agent, tests, events, sim), RelayDriverOptions.NoGitCommit);
        return await driver.RunTaskAsync(repo.Root, taskId);
    }

    // A committed Python project; its git-ignored .venv holds the editable install uv writes,
    // a .pth line naming the checkout's absolute src directory.
    private static TestRepository SeededRepo(out VisualRelay.GitSim.GitSim sim, bool withVirtualenv)
    {
        var repo = TestRepository.Create();
        sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, ".gitignore", ".relay/\n.venv/\n");
        sim.Seed(repo.Root, "src/pkg/__init__.py", "VALUE = 1\n");
        sim.Commit(repo.Root, "chore: seed repo");
        if (!withVirtualenv)
            return repo;

        var sitePackages = Path.Combine(repo.Root, ".venv", "lib", "python3.14", "site-packages");
        Directory.CreateDirectory(sitePackages);
        File.WriteAllText(Path.Combine(repo.Root, ".venv", "pyvenv.cfg"), "home = /usr/bin\n");
        File.WriteAllText(Path.Combine(sitePackages, "_editable_impl_pkg.pth"), Path.Combine(repo.Root, "src") + "\n");
        return repo;
    }

    /// <summary>
    /// Records where each command ran and the search paths it was given. In the checkout the
    /// first suite run is the author gate's red and the guard, when asked to, is red once;
    /// everything else is green.
    /// </summary>
    private sealed class SearchPathRecordingTestRunner(string checkoutRoot, bool guardRedInCheckoutFirst) : ITestRunner
    {
        private readonly Lock _gate = new();
        private bool _suiteRedServed;
        private bool _guardRedServed = !guardRedInCheckoutFirst;

        public List<(string RootPath, string Command, IReadOnlyDictionary<string, string>? SearchPaths)> Calls { get; } = [];

        public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default) =>
            Task.FromResult(Answer(rootPath, command, null));

        public Task<TestRunResult> RunAsync(
            string rootPath, string command, IReadOnlyDictionary<string, string> searchPaths, CancellationToken cancellationToken) =>
            Task.FromResult(Answer(rootPath, command, searchPaths));

        private TestRunResult Answer(string rootPath, string command, IReadOnlyDictionary<string, string>? searchPaths)
        {
            lock (_gate)
            {
                Calls.Add((rootPath, command, searchPaths));
                if (rootPath != checkoutRoot)
                    return new TestRunResult(0, "All tests pass");
                if (command == GuardCmd && !_guardRedServed)
                {
                    _guardRedServed = true;
                    return new TestRunResult(1, "ERROR: src/pkg/__init__.py is 305 lines (limit: 300)");
                }

                if (command == GuardCmd || _suiteRedServed)
                    return new TestRunResult(0, "All tests pass");
                _suiteRedServed = true;
                return new TestRunResult(1, "red");
            }
        }
    }
}
