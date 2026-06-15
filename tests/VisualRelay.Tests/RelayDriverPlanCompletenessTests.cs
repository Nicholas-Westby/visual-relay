using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayDriverPlanCompletenessTests
{
    private sealed class Stage4Runner : ISubagentRunner
    {
        readonly string _plan; readonly string[] _manifest;
        readonly ScriptedSubagentRunner _inner = new();
        public readonly List<StageInvocation> Invocations = [];
        public Stage4Runner(string plan, string[] manifest) { _plan = plan; _manifest = manifest; }
        public void Seed(string code, string test) => _inner.SeedHappyPath(code, test);
        public Task<SubagentResult> RunAsync(StageInvocation inv, CancellationToken ct = default)
        {
            Invocations.Add(inv);
            if (inv.Stage.Number != 4) return _inner.RunAsync(inv, ct);
            var mj = string.Join(",", _manifest.Select(x => $"\"{x}\""));
            var ep = _plan.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var j = $$"""{"plan":"{{ep}}","manifest":[{{mj}}]}""";
            return Task.FromResult(new SubagentResult(j, j, true, null));
        }
    }

    [Fact]
    public async Task Stage4_CompletePlan_ProceedsToStage5WithoutRetry()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("t", "## Done when\n- Implement auth\n- Create tests\n");
        var r = new Stage4Runner("Implement auth and create tests.", ["src/Auth.cs", "tests/T.cs"]);
        r.Seed("src/Auth.cs", "tests/T.cs");

        // Minimal git repo so the stage-5 worktree filter can enumerate.
        Directory.CreateDirectory(Path.Combine(repo.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, "src", "Auth.cs"), "old");
        TestGit.Run(repo.Root, "init");
        TestGit.Run(repo.Root, "config", "user.email", "test@example.test");
        TestGit.Run(repo.Root, "config", "user.name", "Test");
        TestGit.Run(repo.Root, "add", ".");
        TestGit.Run(repo.Root, "commit", "-m", "seed");

        var sink = new InMemoryRelayEventSink();
        var d = new RelayDriver(RelayDriverDependencies.ForTests(r,
            new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);
        var o = await d.RunTaskAsync(repo.Root, "t");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, o.Status);
        Assert.Equal(1, r.Invocations.Count(i => i.Stage.Number == 4));
        Assert.Contains(sink.Events, e => e is { EventName: "stage_start", StageNumber: 5 });
    }

    [Fact]
    public async Task Stage4_IncompletePlan_TriggersOneRetry()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("t", "## Done when\n- Build dashboard UI\n- Implement REST API\n- Write integration tests\n");
        var r = new Stage4Runner("Build dashboard UI and implement REST API.",
            ["src/Dashboard.axaml", "src/Api.cs"]);
        r.Seed("src/Dashboard.axaml", "tests/T.cs");
        var sink = new InMemoryRelayEventSink();
        var d = new RelayDriver(RelayDriverDependencies.ForTests(r,
            new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")), sink),
            RelayDriverOptions.NoGitCommit);
        var o = await d.RunTaskAsync(repo.Root, "t");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, o.Status);
        var s4 = r.Invocations.Where(i => i.Stage.Number == 4).ToList();
        Assert.Equal(2, s4.Count);
        Assert.Contains("integration tests", s4[1].LastTestOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(sink.Events, e => e is { EventName: "stage_start", StageNumber: 5 });
    }

    [Fact]
    public async Task Stage4_RetryAlsoIncompletePlan_ProceedsAfterOneRetry()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("t", "## Done when\n- Create migration\n- Update EF models\n");
        var r = new Stage4Runner("Create migration.", ["src/Migration.sql"]);
        r.Seed("src/Migration.sql", "tests/T.cs");
        var d = new RelayDriver(RelayDriverDependencies.ForTests(r,
            new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
            new InMemoryRelayEventSink()), RelayDriverOptions.NoGitCommit);
        var o = await d.RunTaskAsync(repo.Root, "t");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, o.Status);
        Assert.Equal(2, r.Invocations.Count(i => i.Stage.Number == 4));
    }

    [Fact]
    public async Task Stage4_NoDeliverableHeading_NeverRetries()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("t", "# Simple task\nJust do work.\n");
        var r = new Stage4Runner("Do work.", ["src/Work.cs"]);
        r.Seed("src/Work.cs", "tests/T.cs");
        var d = new RelayDriver(RelayDriverDependencies.ForTests(r,
            new ScriptedTestRunner(new TestRunResult(1, "red"), new TestRunResult(0, "green")),
            new InMemoryRelayEventSink()), RelayDriverOptions.NoGitCommit);
        var o = await d.RunTaskAsync(repo.Root, "t");
        Assert.Equal(RelayTaskOutcomeStatus.Committed, o.Status);
        Assert.Equal(1, r.Invocations.Count(i => i.Stage.Number == 4));
    }

    [Fact]
    public async Task Stage4_PlanOnly_DoesNotRetry()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("t", "## Done when\n- Build something different\n");
        var r = new Stage4Runner("Refactor unrelated module.", ["src/Unrelated.cs"]);
        var d = new RelayDriver(RelayDriverDependencies.ForTests(r, new ScriptedTestRunner(),
            new InMemoryRelayEventSink()), new RelayDriverOptions(CreateGitCommit: false, LastStageToRun: 4));
        var o = await d.RunTaskAsync(repo.Root, "t");
        Assert.Equal(RelayTaskOutcomeStatus.Planned, o.Status);
        Assert.Equal(1, r.Invocations.Count(i => i.Stage.Number == 4));
    }
}
