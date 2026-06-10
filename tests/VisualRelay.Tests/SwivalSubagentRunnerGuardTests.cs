using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class SwivalSubagentRunnerGuardTests
{
    [Fact]
    public async Task RunAsync_BackendNotReady_FailsFastWithoutSpawningSwival()
    {
        using var repo = TestRepository.Create();
        // A binary that does not exist: if the guard fails to short-circuit, the
        // runner would try to spawn it and throw, so reaching a clean failure
        // result proves swival was never spawned.
        var runner = new SwivalSubagentRunner(
            TestConfig(),
            swivalBinary: "/nonexistent/swival",
            backendProbe: _ => Task.FromResult(new BackendReadiness(false, "backend down at http://127.0.0.1:4000")));

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.False(result.IsValid);
        Assert.Equal("backend down at http://127.0.0.1:4000", result.Error);
        Assert.Null(result.Json);
        Assert.False(Directory.Exists(SwivalTestHelpers.Invocation(repo.Root).TraceDirectory));
    }

    private static RelayConfig TestConfig() =>
        new(
            "llm-tasks",
            "true",
            "true",
            [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            1,
            1,
            1,
            false,
            true,
            5_000,
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2,
            BypassSandbox: true);
}
