using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Fix C: the optional build phase must honor its OWN budget
/// (<see cref="RelayConfig.BuildTimeoutMilliseconds"/>), threaded in via the
/// explicit-hard-cap <see cref="ITestRunner"/> overload — NOT the shorter
/// <see cref="RelayConfig.TestTimeoutMilliseconds"/>. The test command keeps using
/// the base (default-timeout) overload so the timed test budget covers only the suite.
/// </summary>
public sealed class BuildTimeoutThreadingTests
{
    [Fact]
    public async Task BuildPhase_UsesBuildTimeout_NotTestTimeout()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test --no-build",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 1,
              "buildCmd": "dotnet build --no-restore",
              "buildTimeoutMs": 600000,
              "testTimeoutMs": 300000
            }
            """);
        repo.WriteTask("build-cap", "# Build cap\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new CapCapturingTestRunner(
            new TestRunResult(1, "red"),    // stage 5 author gate (base overload)
            new TestRunResult(0, "built"),  // stage 9 build phase (cap overload)
            new TestRunResult(0, "green")); // stage 9 verify (base overload)
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "build-cap");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        // The build ran via the explicit-cap overload, bounded by buildTimeoutMs.
        var buildCall = tests.Calls.Single(c => c.Command == "dotnet build --no-restore");
        Assert.Equal(TimeSpan.FromMilliseconds(600_000), buildCall.HardCap);
        // The test command used the base overload — NOT capped by buildTimeoutMs.
        Assert.Contains(tests.Calls, c => c.Command == "dotnet test --no-build" && c.HardCap is null);
    }

    [Fact]
    public async Task BuildPhase_ZeroBuildTimeout_MeansInfiniteCap()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test --no-build",
              "logSources": [],
              "baselineVerify": false,
              "maxVerifyLoops": 1,
              "buildCmd": "dotnet build --no-restore",
              "buildTimeoutMs": 0
            }
            """);
        repo.WriteTask("build-cap-0", "# Build cap zero\n");
        var runner = new ScriptedSubagentRunner();
        runner.SeedHappyPath("src/app.cs", "tests/app.tests.cs");
        var tests = new CapCapturingTestRunner(
            new TestRunResult(1, "red"),
            new TestRunResult(0, "built"),
            new TestRunResult(0, "green"));
        var driver = new RelayDriver(
            RelayDriverDependencies.ForTests(runner, tests, new InMemoryRelayEventSink()),
            RelayDriverOptions.NoGitCommit);

        var outcome = await driver.RunTaskAsync(repo.Root, "build-cap-0");

        Assert.Equal(RelayTaskOutcomeStatus.Committed, outcome.Status);
        var buildCall = tests.Calls.Single(c => c.Command == "dotnet build --no-restore");
        Assert.Equal(Timeout.InfiniteTimeSpan, buildCall.HardCap);
    }
}

/// <summary>
/// Records each call with the explicit hard cap (null when the base overload — the
/// test phase — was used, set when the explicit-cap overload — the build phase — was).
/// </summary>
internal sealed class CapCapturingTestRunner(params TestRunResult[] results) : ITestRunner
{
    private readonly Queue<TestRunResult> _results = new(results);
    public List<(string Command, TimeSpan? HardCap)> Calls { get; } = [];

    public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        Calls.Add((command, null));
        return Next();
    }

    public Task<TestRunResult> RunAsync(string rootPath, string command, TimeSpan hardCap, CancellationToken cancellationToken = default)
    {
        Calls.Add((command, hardCap));
        return Next();
    }

    private Task<TestRunResult> Next() =>
        Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new TestRunResult(0, "green"));
}
