using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A path git ignores that appears in the checkout during a run is said out loud: in a warn event
/// and in the ledger every later stage and the reviewer read. The diff, the review and the commit
/// never show such a path. Measured twice: on the Windows arm an Author-tests agent wrote
/// .mvn/maven.config with -Drat.skip=true into apache/commons-lang's checkout, so both commits were
/// verified with the license check off, and on the Mac a Fix-verify agent rewrote the checkout's
/// .venv editable install in openai-agents-python to get its verify green.
/// </summary>
public sealed class RelayDriverIgnoredPathAuditTests
{
    [Fact]
    public async Task AnIgnoredPathAnAgentCreates_IsReportedToTheLaterStages()
    {
        using var repo = SeededRepo(out var sim);
        var agent = new FileWritingSubagentRunner(HappyPath(), stage: 5, ".mvn/maven.config", "-Drat.skip=true\n");
        var events = new InMemoryRelayEventSink();

        await RunAsync(repo, sim, agent, events);

        var created = Assert.Single(events.Events, e => e.EventName == "ignored_paths_created");
        Assert.Equal("warn", created.Level);
        Assert.Equal(".mvn/", created.Data!["paths"]);
        var ledger = await File.ReadAllTextAsync(Path.Combine(repo.Root, ".relay", "audit-task", "ledger.md"));
        Assert.Contains("Outside version control", ledger, StringComparison.Ordinal);
        Assert.Contains(".mvn/", ledger, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoredPathsThatWereThereBeforeTheRun_AreNotReported()
    {
        using var repo = SeededRepo(out var sim);
        Directory.CreateDirectory(Path.Combine(repo.Root, ".mvn"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".mvn", "jvm.config"), "-Xmx2g\n");
        var events = new InMemoryRelayEventSink();

        await RunAsync(repo, sim, HappyPath(), events);

        Assert.DoesNotContain(events.Events, e => e.EventName == "ignored_paths_created");
    }

    [Fact]
    public async Task BuildOutputAndCachesARunLeavesBehind_AreNotReported()
    {
        using var repo = SeededRepo(out var sim);
        ISubagentRunner agent = HappyPath();
        foreach (var byproduct in (string[])["target/debug/app", "__pycache__/app.cpython-314.pyc", ".pytest_cache/v/cache/nodeids", "node_modules/.cache/x"])
            agent = new FileWritingSubagentRunner(agent, stage: 6, byproduct, "x");
        var events = new InMemoryRelayEventSink();

        await RunAsync(repo, sim, agent, events);

        Assert.DoesNotContain(events.Events, e => e.EventName == "ignored_paths_created");
    }

    private static ScriptedSubagentRunner HappyPath()
    {
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        return runner;
    }

    private static async Task RunAsync(
        TestRepository repo, VisualRelay.GitSim.GitSim sim, ISubagentRunner agent, InMemoryRelayEventSink events)
    {
        var outcome = await new RelayDriver(
            RelayDriverDependencies.ForTests(agent,
                new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), events, sim),
            RelayDriverOptions.NoGitCommit).RunTaskAsync(repo.Root, "audit-task");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
    }

    private static TestRepository SeededRepo(out VisualRelay.GitSim.GitSim sim)
    {
        var repo = TestRepository.Create();
        sim = RelayDriverTestHelpers.InitSim(repo);
        sim.Seed(repo.Root, ".gitignore", ".relay/\n.mvn/\ntarget/\n__pycache__/\n.pytest_cache/\nnode_modules/\n");
        sim.Seed(repo.Root, "src/app.cs", "old\n");
        sim.Commit(repo.Root, "chore: seed repo");
        repo.WriteConfig("dotnet test", [], baselineVerify: false);
        repo.WriteTask("audit-task", "# Audit\n");
        return repo;
    }
}
