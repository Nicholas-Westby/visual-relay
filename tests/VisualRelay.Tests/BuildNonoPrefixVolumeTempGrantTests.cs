using VisualRelay.Core.Execution;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Volume .TemporaryItems auto-grant tests for BuildNonoPrefix.
/// Exercises the grant emission/omission across rollback=true (swival agent)
/// and rollback=false (verify) paths.
/// </summary>
public sealed class BuildNonoPrefixVolumeTempGrantTests
{
    // Verify path (rollback:false)

    [Fact]
    public void BuildNonoPrefix_ExternalVolumeRoot_EmitsVolumeTempGrant()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");
        var config = TestConfig();

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false,
            workspaceRoot: "/Volumes/Tera/dev/x");

        // run --profile <abs> --allow-cwd -a <templatesDir> -a /Volumes/Tera/.TemporaryItems --silent --
        Assert.Equal("run", prefix[0]);
        Assert.Equal("--profile", prefix[1]);
        Assert.Equal(ProfilePath, prefix[2]);
        Assert.Equal("--allow-cwd", prefix[3]);
        Assert.Equal("-a", prefix[4]);
        Assert.Equal(TemplatesDir, prefix[5]);
        Assert.Equal("-a", prefix[6]);
        Assert.Equal("/Volumes/Tera/.TemporaryItems", prefix[7]);
        Assert.Equal("--silent", prefix[8]);
        Assert.Equal("--", prefix[9]);
    }

    [Fact]
    public void BuildNonoPrefix_HomeRootedPath_OmitsVolumeTempGrant()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");
        var config = TestConfig();

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false,
            workspaceRoot: "/Users/nick/dev/x");

        // Prefix must be identical to the null-workspaceRoot case: no volume grant.
        Assert.Equal(
            new[] { "run", "--profile", ProfilePath, "--allow-cwd", "-a", TemplatesDir, "--silent", "--" },
            prefix);
    }

    [Fact]
    public void BuildNonoPrefix_VolumeGrantAppearsBeforeDashDash()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");
        var config = TestConfig();

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false,
            workspaceRoot: "/Volumes/Tera/dev/x");

        var dashDashIndex = ((IList<string>)prefix).IndexOf("--");
        Assert.True(dashDashIndex > 0);

        var grantIndex = ((IList<string>)prefix).IndexOf("/Volumes/Tera/.TemporaryItems");
        Assert.True(grantIndex > 0);
        Assert.True(grantIndex < dashDashIndex, "volume temp grant must appear before --");
        Assert.Equal("-a", prefix[grantIndex - 1]);
    }

    [Fact]
    public void BuildNonoPrefix_VolumeGrantComesAfterTemplatesGrant()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");
        var config = TestConfig();

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false,
            workspaceRoot: "/Volumes/Tera/dev/x");

        var templatesIdx = ((IList<string>)prefix).IndexOf(TemplatesDir);
        var volumeIdx = ((IList<string>)prefix).IndexOf("/Volumes/Tera/.TemporaryItems");
        Assert.True(templatesIdx > 0);
        Assert.True(volumeIdx > templatesIdx,
            "volume temp grant must appear after templates grant");
    }

    // Swival agent path (rollback:true)

    [Fact]
    public void BuildNonoPrefix_ExternalVolumeRoot_WithRollback_EmitsGrantBeforeRollback()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS-only /Volumes/ paradigm");
        var config = TestConfig();

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true,
            workspaceRoot: "/Volumes/Tera/dev/x");

        // run --profile <abs> --allow-cwd -a <templatesDir> -a /Volumes/Tera/.TemporaryItems
        //   --rollback --no-rollback-prompt --silent --
        Assert.Equal("run", prefix[0]);
        Assert.Equal("--profile", prefix[1]);
        Assert.Equal(ProfilePath, prefix[2]);
        Assert.Equal("--allow-cwd", prefix[3]);
        Assert.Equal("-a", prefix[4]);
        Assert.Equal(TemplatesDir, prefix[5]);
        Assert.Equal("-a", prefix[6]);
        Assert.Equal("/Volumes/Tera/.TemporaryItems", prefix[7]);
        // Grant must appear BEFORE --rollback
        Assert.Equal("--rollback", prefix[8]);
        Assert.Equal("--no-rollback-prompt", prefix[9]);
        Assert.Equal("--silent", prefix[10]);
        Assert.Equal("--", prefix[11]);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static string ProfilePath => NonoProfileEnsurer.ResolveProfilePath();
    private static string TemplatesDir => TaskTemplates.ResolveUserTemplatesDir();

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>
            { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            FirstOutputTimeoutMs: 660_000);
}
