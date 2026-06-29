using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Drift guard: asserts that the swival-agent nono launch prefix and the verify
/// nono launch prefix are identical except for the --rollback/--no-rollback-prompt
/// pair that only the agent launch carries. If either test fails, sandbox parity
/// is genuinely broken — do NOT edit the test to match; escalate instead.
/// </summary>
public sealed class NonoLaunchDriftGuardTests
{
    [Fact]
    public void BuildNonoPrefix_AgentAndVerifyLaunches_DifferOnlyInRollbackFlag()
    {
        // The agent launch uses rollback:true, verify launch uses rollback:false.
        // Everything else (profile, --allow-cwd, extra allow paths) must be identical.
        var config = TestConfig();

        var agentPrefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true).ToList();
        var verifyPrefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false).ToList();

        // Both must start with: run --profile <abs> --allow-cwd
        var head = new[] { "run", "--profile", NonoProfileEnsurer.ResolveProfilePath(), "--allow-cwd" };
        Assert.Equal(head, agentPrefix.Take(4));
        Assert.Equal(head, verifyPrefix.Take(4));

        // Agent must have --rollback and --no-rollback-prompt; verify must NOT.
        Assert.Contains("--rollback", agentPrefix);
        Assert.Contains("--no-rollback-prompt", agentPrefix);
        Assert.DoesNotContain("--rollback", verifyPrefix);
        Assert.DoesNotContain("--no-rollback-prompt", verifyPrefix);

        // The non-rollback portions must be identical (positional filter preserves order and duplicates).
        var agentCore = agentPrefix.Where(x => x is not "--rollback" and not "--no-rollback-prompt").ToList();
        Assert.Equal(agentCore, verifyPrefix);
    }

    [Fact]
    public void BuildNonoPrefix_WithExtraAllowPaths_BothLaunchesCarryTheSamePaths()
    {
        var config = TestConfig() with
        {
            SandboxExtraAllowPaths = ["/tmp/extra-cache", "/tmp/extra-build"]
        };

        var agentPrefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true).ToList();
        var verifyPrefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false).ToList();

        // Both must contain the extra paths as -a <path> pairs.
        foreach (var extraPath in config.SandboxExtraAllowPaths!)
        {
            var agentIdx = agentPrefix.IndexOf("-a");
            while (agentIdx >= 0 && agentIdx + 1 < agentPrefix.Count && agentPrefix[agentIdx + 1] != extraPath)
                agentIdx = agentPrefix.IndexOf("-a", agentIdx + 1);
            Assert.True(agentIdx >= 0, $"agent prefix missing -a {extraPath}");

            var verifyIdx = verifyPrefix.IndexOf("-a");
            while (verifyIdx >= 0 && verifyIdx + 1 < verifyPrefix.Count && verifyPrefix[verifyIdx + 1] != extraPath)
                verifyIdx = verifyPrefix.IndexOf("-a", verifyIdx + 1);
            Assert.True(verifyIdx >= 0, $"verify prefix missing -a {extraPath}");
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>
            { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2);
}
