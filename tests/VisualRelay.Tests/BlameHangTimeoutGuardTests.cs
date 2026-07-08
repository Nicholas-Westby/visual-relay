namespace VisualRelay.Tests;

/// <summary>
/// Guard tests that verify the --blame-hang-timeout is consistently 120s across
/// all sites (dotnet-test-files.sh, .relay/config.json, AGENTS.md, TROUBLESHOOTING.md,
/// TestRunner.cs, CheckCommand.cs) and that -m:1 is preserved in dotnet test
/// invocations. These assert the TARGET state — they fail until the values are raised.
/// </summary>
public sealed class BlameHangTimeoutGuardTests
{
    private static string RepoRoot => RepoSetup.Root;

    // ── blame-hang-timeout: must be 120s everywhere ──────────────

    [Fact]
    public void DotnetTestFilesScript_HasBlameHangTimeout_120s()
    {
        var path = Path.Combine(RepoRoot, "tools", "dotnet-test-files.sh");
        Assert.True(File.Exists(path), $"Missing: {path}");
        var content = File.ReadAllText(path);

        Assert.Contains("--blame-hang-timeout 120s", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--blame-hang-timeout 20s", content, StringComparison.Ordinal);
    }

    [Fact]
    public void RelayConfigTestCmd_HasBlameHangTimeout_120s()
    {
        var path = Path.Combine(RepoRoot, ".relay", "config.json");
        Assert.True(File.Exists(path), $"Missing: {path}");
        var content = File.ReadAllText(path);

        // The testCmd field must carry 120s, not the old 60s.
        Assert.Contains("\"testCmd\"", content, StringComparison.Ordinal);
        Assert.Contains("--blame-hang-timeout 120s", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--blame-hang-timeout 60s", content, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentsMd_HasBlameHangTimeout_120s()
    {
        var path = Path.Combine(RepoRoot, "AGENTS.md");
        Assert.True(File.Exists(path), $"Missing: {path}");
        var content = File.ReadAllText(path);

        // The troubleshooting hint must show 120s, not the old 30s.
        Assert.Contains("--blame-hang-timeout 120s", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--blame-hang-timeout 30s", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TroubleshootingMd_HasBlameHangTimeout_120s()
    {
        var path = Path.Combine(RepoRoot, "TROUBLESHOOTING.md");
        Assert.True(File.Exists(path), $"Missing: {path}");
        var content = File.ReadAllText(path);

        // The troubleshooting hint must show 120s, not the old 30s.
        Assert.Contains("--blame-hang-timeout 120s", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--blame-hang-timeout 30s", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TestRunner_HasBlameHangTimeout_120s()
    {
        var path = Path.Combine(RepoRoot, "tools", "VisualRelay.Cli", "TestRunner.cs");
        Assert.True(File.Exists(path), $"Missing: {path}");
        var content = File.ReadAllText(path);

        // The timeout hint must show 120s, not the old 30s.
        Assert.Contains("--blame-hang-timeout 120s", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--blame-hang-timeout 30s", content, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckCommand_HasBlameHangTimeout_120s()
    {
        var path = Path.Combine(RepoRoot, "tools", "VisualRelay.Cli", "Commands", "CheckCommand.cs");
        Assert.True(File.Exists(path), $"Missing: {path}");
        var content = File.ReadAllText(path);

        // The timeout hint must show 120s, not the old 30s.
        Assert.Contains("--blame-hang-timeout 120s", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--blame-hang-timeout 30s", content, StringComparison.Ordinal);
    }

    // ── -m:1 preservation: must NOT be removed ───────────────────

    [Fact]
    public void DotnetTestFilesScript_Preserves_MinusM1()
    {
        var path = Path.Combine(RepoRoot, "tools", "dotnet-test-files.sh");
        Assert.True(File.Exists(path), $"Missing: {path}");
        var content = File.ReadAllText(path);

        // Both dotnet test lines must still carry -m:1.
        Assert.Contains("-m:1", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TestRunner_Preserves_MinusM1()
    {
        var path = Path.Combine(RepoRoot, "tools", "VisualRelay.Cli", "TestRunner.cs");
        Assert.True(File.Exists(path), $"Missing: {path}");
        var content = File.ReadAllText(path);

        Assert.Contains("\"-m:1\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckCommand_Preserves_MinusM1()
    {
        var path = Path.Combine(RepoRoot, "tools", "VisualRelay.Cli", "Commands", "CheckCommand.cs");
        Assert.True(File.Exists(path), $"Missing: {path}");
        var content = File.ReadAllText(path);

        Assert.Contains("\"-m:1\"", content, StringComparison.Ordinal);
    }
}
