using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class SwivalSubagentRunnerSandboxTests
{
    private static Task<BackendReadiness> AlwaysReady(CancellationToken _) =>
        Task.FromResult(new BackendReadiness(true, null));

    [Fact]
    public void BuildArguments_SandboxEnabled_IncludesSandboxFlags()
    {
        var config = TestConfig() with { BypassSandbox = false }; // sandbox explicitly enabled (default is now bypass)
        var runner = new SwivalSubagentRunner(config, "swival", backendProbe: AlwaysReady);
        var invocation = Invocation(Path.GetTempPath());

        var args = runner.BuildArguments(invocation);

        Assert.Contains("--sandbox", args);
        Assert.Contains("nono", args);
        Assert.Contains("--nono-profile", args);
        Assert.Contains("vr-guard", args);
        Assert.Contains("--nono-rollback", args);

        // Verify exact flag order: --sandbox nono --nono-profile vr-guard --nono-rollback
        var sandboxIdx = args.IndexOf("--sandbox");
        Assert.True(sandboxIdx >= 0);
        Assert.Equal("nono", args[sandboxIdx + 1]);
        Assert.Equal("--nono-profile", args[sandboxIdx + 2]);
        Assert.Equal("vr-guard", args[sandboxIdx + 3]);
        Assert.Equal("--nono-rollback", args[sandboxIdx + 4]);
    }

    [Fact]
    public void BuildArguments_BypassSandboxTrue_OmitsSandboxFlags()
    {
        var config = TestConfig() with { BypassSandbox = true };
        var runner = new SwivalSubagentRunner(config, "swival", backendProbe: AlwaysReady);
        var invocation = Invocation(Path.GetTempPath());

        var args = runner.BuildArguments(invocation);

        Assert.DoesNotContain("--sandbox", args);
        Assert.DoesNotContain("--nono-profile", args);
        Assert.DoesNotContain("--nono-rollback", args);
    }

    [Fact]
    public void BuildArguments_BypassSandboxFalse_DoesNotBlockNetwork()
    {
        var config = TestConfig() with { BypassSandbox = false }; // sandbox on
        var runner = new SwivalSubagentRunner(config, "swival", backendProbe: AlwaysReady);
        var invocation = Invocation(Path.GetTempPath());

        var args = runner.BuildArguments(invocation);

        Assert.DoesNotContain("--nono-block-net", args);
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
            2);

    private static StageInvocation Invocation(string rootPath) =>
        new(
            RelayStages.All[0],
            "cheap",
            "run-1",
            rootPath,
            "task",
            "# Task",
            string.Empty,
            [],
            [],
            Path.Combine(rootPath, ".relay", "task", "stage1-attempt1"),
            Path.Combine(rootPath, ".relay", "task", "stage1-attempt1.report.json"),
            1);
}
