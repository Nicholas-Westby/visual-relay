using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Tests that <see cref="SwivalSubagentRunner.BuildNonoPrefix"/> emits
/// <c>--skip-dir &lt;name&gt;</c> entries (before the <c>--</c> separator) so
/// nono's rollback PREFLIGHT skips large/never-rolled-back dirs and stays
/// under its fixed budget on big target repos.
/// </summary>
public sealed partial class SwivalSubagentRunnerSandboxTests
{
    [Fact]
    public void BuildNonoPrefix_WithSkipDirs_EmitsSkipDirFlagsBeforeSeparator()
    {
        var config = TestConfig() with { BypassSandbox = false };
        string[] skipDirs = [".git", ".relay", "data"];

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true, skipDirs: skipDirs);

        // Every --skip-dir flag (and its value) must appear before the `--` separator.
        var sepIdx = prefix.ToList().IndexOf("--");
        Assert.True(sepIdx >= 0, "prefix must contain a -- separator");
        for (var i = 0; i < prefix.Count; i++)
        {
            if (prefix[i] == "--skip-dir")
            {
                Assert.True(i < sepIdx, "--skip-dir flag must precede the -- separator");
                Assert.True(i + 1 < sepIdx, "--skip-dir value must precede the -- separator");
            }
        }

        // The subsequence --skip-dir .git … --skip-dir .relay … --skip-dir data
        // appears in order (paired flag+value for each name).
        AssertSkipDirPairInOrder(prefix, ".git", ".relay", "data");

        // Rollback flags are still present (rollback is intentional on this path).
        Assert.Contains("--rollback", prefix);
        Assert.Contains("--no-rollback-prompt", prefix);
    }

    [Fact]
    public void BuildNonoPrefix_WithSkipDirs_BypassEnabled_ReturnsEmpty()
    {
        var config = TestConfig() with { BypassSandbox = true };
        string[] skipDirs = [".git", "data"];

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true, skipDirs: skipDirs);

        Assert.Empty(prefix);
    }

    [Fact]
    public void BuildNonoPrefix_NullSkipDirs_BehavesExactlyAsBefore()
    {
        // No skipDirs argument → identical to the historical rollback prefix.
        var config = TestConfig() with { BypassSandbox = false };

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true, skipDirs: null);

        Assert.Equal(
            new[] { "run", "--profile", ProfilePath, "--allow-cwd", "--rollback", "--no-rollback-prompt", "--" },
            prefix);
        Assert.DoesNotContain("--skip-dir", prefix);
    }

    [Fact]
    public void BuildNonoPrefix_EmptySkipDirs_BehavesExactlyAsBefore()
    {
        var config = TestConfig() with { BypassSandbox = false };

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true, skipDirs: Array.Empty<string>());

        Assert.Equal(
            new[] { "run", "--profile", ProfilePath, "--allow-cwd", "--rollback", "--no-rollback-prompt", "--" },
            prefix);
        Assert.DoesNotContain("--skip-dir", prefix);
    }

    private static void AssertSkipDirPairInOrder(IReadOnlyList<string> prefix, params string[] names)
    {
        var searchFrom = 0;
        foreach (var name in names)
        {
            var found = false;
            for (var i = searchFrom; i + 1 < prefix.Count; i++)
            {
                if (prefix[i] == "--skip-dir" && prefix[i + 1] == name)
                {
                    searchFrom = i + 2;
                    found = true;
                    break;
                }
            }

            Assert.True(found, $"expected `--skip-dir {name}` after index {searchFrom}");
        }
    }
}
