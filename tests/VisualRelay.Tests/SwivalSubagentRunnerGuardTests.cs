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

    /// <summary>
    /// On a healthy backend the injected probe is called exactly once (no added
    /// latency, no retry delay — injected fakes are used verbatim) and RunAsync
    /// proceeds.
    /// </summary>
    [Fact]
    public async Task RunAsync_BackendReadyOnFirstProbe_RunsWithSingleCall()
    {
        using var repo = TestRepository.Create();
        var script = await SwivalTestHelpers.WriteExecutableAsync(
            repo.Root, "swival",
            "#!/bin/bash\necho '{\"summary\":\"ok\"}'\n");
        var callCount = 0;
        var runner = new SwivalSubagentRunner(
            TestConfig(),
            swivalBinary: script,
            backendProbe: _ =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult(new BackendReadiness(true, null));
            },
            nonoBinary: await SwivalTestHelpers.WritePassthroughNonoAsync(repo.Root));

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        // Injected probes are used verbatim: exactly one call, no retry wrapping.
        Assert.Equal(1, callCount);

        // RunAsync proceeds past the probe — result comes from swival execution.
        Assert.DoesNotContain("backend down", result.Error);
        Assert.DoesNotContain("Can't reach", result.Error);
    }

    private static RelayConfig TestConfig() =>
        new(
            "llm-tasks",
            "true",
            "true",
            [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true,
            1,
            1,
            false,
            true,
            5_000,
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2);
}
