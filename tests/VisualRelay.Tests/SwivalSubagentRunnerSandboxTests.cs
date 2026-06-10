using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class SwivalSubagentRunnerSandboxTests
{
    private static Task<BackendReadiness> AlwaysReady(CancellationToken _) =>
        Task.FromResult(new BackendReadiness(true, null));

    [Fact]
    public void BuildArguments_NeverInjectsSandboxFlagsIntoSwival()
    {
        // swival has no --sandbox/--nono-* flags; the sandbox is applied by
        // WRAPPING swival in `nono run`, never by passing flags to swival.
        foreach (var bypass in new[] { true, false })
        {
            var config = TestConfig() with { BypassSandbox = bypass };
            var runner = new SwivalSubagentRunner(config, "swival", backendProbe: AlwaysReady);

            var args = runner.BuildArguments(Invocation(Path.GetTempPath()));

            Assert.DoesNotContain("--sandbox", args);
            Assert.DoesNotContain("--nono-profile", args);
            Assert.DoesNotContain("--nono-rollback", args);
            Assert.DoesNotContain("nono", args);
        }
    }

    [Fact]
    public void BuildLaunchTarget_SandboxEnabled_WrapsSwivalInNono()
    {
        var config = TestConfig() with { BypassSandbox = false }; // sandbox explicitly enabled (default is bypass)
        var runner = new SwivalSubagentRunner(config, "swival", backendProbe: AlwaysReady);
        var swivalArgs = runner.BuildArguments(Invocation(Path.GetTempPath()));

        var (fileName, args) = runner.BuildLaunchTarget(swivalArgs);

        // Launched process is nono, not swival.
        Assert.Equal("nono", fileName);

        // Exact nono prefix: run -p vr-guard --allow-cwd --rollback --no-rollback-prompt -- swival ...
        Assert.Equal(
            new[] { "run", "-p", "vr-guard", "--allow-cwd", "--rollback", "--no-rollback-prompt", "--", "swival" },
            args.Take(8));

        // Everything after `-- swival` is the swival arg list, unchanged.
        var separatorIdx = ((IList<string>)args).IndexOf("--");
        Assert.Equal("swival", args[separatorIdx + 1]);
        Assert.Equal(swivalArgs, args.Skip(separatorIdx + 2));

        // nono never blocks the network (the relay must reach the model backend).
        Assert.DoesNotContain("--block-net", args);
    }

    [Fact]
    public void BuildLaunchTarget_BypassSandbox_LaunchesSwivalDirectly()
    {
        var config = TestConfig() with { BypassSandbox = true };
        var runner = new SwivalSubagentRunner(config, "swival", backendProbe: AlwaysReady);
        var swivalArgs = runner.BuildArguments(Invocation(Path.GetTempPath()));

        var (fileName, args) = runner.BuildLaunchTarget(swivalArgs);

        Assert.Equal("swival", fileName);
        Assert.Same(swivalArgs, args);
        Assert.DoesNotContain("nono", args);
        Assert.DoesNotContain("run", args);
    }

    [Fact]
    public void BuildSandboxEnvironment_BypassEnabled_ReturnsNull()
    {
        // When BypassSandbox is true the sandbox is off, so no env redirect
        // is needed — the process runs directly on the host.
        var config = TestConfig() with { BypassSandbox = true };

        var env = SwivalSubagentRunner.BuildSandboxEnvironment(config);

        Assert.Null(env);
    }

    [Fact]
    public void BuildSandboxEnvironment_SandboxEnabled_ReturnsCacheRedirects()
    {
        // When the sandbox is enabled (BypassSandbox = false), swival runs
        // under nono. Transitive deps (huggingface_hub via litellm, uv) try
        // to write to ~/.cache/… which nono's vr-guard profile denies.
        // We redirect those cache writes into ~/.config/swival (already in
        // the swival profile write-allow list) via env vars.
        var config = TestConfig() with { BypassSandbox = false };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var env = SwivalSubagentRunner.BuildSandboxEnvironment(config);

        Assert.NotNull(env);
        Assert.Equal(3, env.Count);
        Assert.Equal(Path.Combine(home, ".config", "swival", "huggingface"), env["HF_HOME"]);
        Assert.Equal(Path.Combine(home, ".config", "swival", "cache"), env["XDG_CACHE_HOME"]);
        Assert.Equal(Path.Combine(home, ".config", "swival", "uv-cache"), env["UV_CACHE_DIR"]);
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
