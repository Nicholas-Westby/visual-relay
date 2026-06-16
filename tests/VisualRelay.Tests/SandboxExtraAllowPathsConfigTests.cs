using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <c>RelayConfig.SandboxExtraAllowPaths</c> — the per-repo escape
/// hatch in <c>.relay/config.json</c>. Validates that the field defaults empty,
/// expands <c>~</c>/<c>$HOME</c>, rejects <c>..</c> (load error), rejects paths
/// outside <c>$HOME</c> and the workspace root, and accepts legitimate paths.
/// </summary>
public sealed class SandboxExtraAllowPathsConfigTests
{
    [Fact]
    public void SandboxExtraAllowPaths_DefaultsToEmpty()
    {
        var config = TestConfig();
        // Default is null — the field is optional and only present when the
        // .relay/config.json has a sandboxExtraAllowPaths key.
        Assert.Null(config.SandboxExtraAllowPaths);
    }

    [Fact]
    public async Task Tilde_ExpandsToHome()
    {
        using var repo = TestRepository.Create();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), $$"""
            {
              "testCmd": "true",
              "logSources": [],
              "sandboxExtraAllowPaths": ["~/{{Path.GetFileName(Path.Combine(home, ".cache", "exotic-tool"))}}"]
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        var paths = result.Config.SandboxExtraAllowPaths;
        Assert.NotNull(paths);
        Assert.Single(paths);
        Assert.StartsWith(home, paths[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DotDot_ProducesLoadError()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "true",
              "logSources": [],
              "sandboxExtraAllowPaths": ["~/../etc/passwd"]
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Malformed, result.Status);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("..", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PathOutsideHome_Rejected()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "true",
              "logSources": [],
              "sandboxExtraAllowPaths": ["/etc/config"]
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Malformed, result.Status);
        Assert.NotNull(result.Diagnostic);
    }

    [Fact]
    public async Task LegitimatePathUnderHome_Accepted()
    {
        using var repo = TestRepository.Create();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var allowed = Path.Combine(home, ".cache", "exotic-tool");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), $$"""
            {
              "testCmd": "true",
              "logSources": [],
              "sandboxExtraAllowPaths": ["{{allowed.Replace("\\", "\\\\")}}"]
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Single(result.Config.SandboxExtraAllowPaths!);
        Assert.Equal(allowed, result.Config.SandboxExtraAllowPaths![0]);
    }

    [Fact]
    public async Task PathUnderWorkspaceRoot_Accepted()
    {
        using var repo = TestRepository.Create();
        var wsPath = Path.Combine(repo.Root, ".cache", "workspace-tool");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), $$"""
            {
              "testCmd": "true",
              "logSources": [],
              "sandboxExtraAllowPaths": ["{{wsPath.Replace("\\", "\\\\")}}"]
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Single(result.Config.SandboxExtraAllowPaths!);
        Assert.Equal(wsPath, result.Config.SandboxExtraAllowPaths![0]);
    }

    // ── Sensitive-subtree rejection ───────────────────────────────────

    [Fact]
    public async Task SandboxExtraAllowPaths_Ssh_ProducesLoadError()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """
            {
              "testCmd": "true",
              "logSources": [],
              "sandboxExtraAllowPaths": ["~/.ssh"]
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Malformed, result.Status);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains(".ssh", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxExtraAllowPaths_Keychains_ProducesLoadError()
    {
        using var repo = TestRepository.Create();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var keychainsPath = Path.Combine(home, "Library", "Keychains");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), $$"""
            {
              "testCmd": "true",
              "logSources": [],
              "sandboxExtraAllowPaths": ["{{keychainsPath.Replace("\\", "\\\\")}}"]
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Malformed, result.Status);
        Assert.NotNull(result.Diagnostic);
        Assert.Contains("Keychains", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxExtraAllowPaths_LegitimateCache_StillAccepted()
    {
        using var repo = TestRepository.Create();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var allowed = Path.Combine(home, ".cache", "exotic-tool");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), $$"""
            {
              "testCmd": "true",
              "logSources": [],
              "sandboxExtraAllowPaths": ["{{allowed.Replace("\\", "\\\\")}}"]
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Single(result.Config.SandboxExtraAllowPaths!);
        Assert.Equal(allowed, result.Config.SandboxExtraAllowPaths![0]);
    }

    // ── Builder integration: -a flags appear in both invocations ───────

    [Fact]
    public void ExtraAllowPaths_AppendedAsAFlags_InSwivalPrefix()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extra = Path.Combine(home, ".cache", "exotic-tool");
        var config = TestConfig() with
        {
            BypassSandbox = false,
            SandboxExtraAllowPaths = [extra]
        };

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: true);

        Assert.Equal("-a", prefix[4]);
        Assert.Equal(extra, prefix[5]);
        Assert.Equal("--rollback", prefix[6]);
    }

    [Fact]
    public void ExtraAllowPaths_AppendedAsAFlags_InVerificationPrefix()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extra = Path.Combine(home, ".cache", "exotic-tool");
        var config = TestConfig() with
        {
            BypassSandbox = false,
            SandboxExtraAllowPaths = [extra]
        };

        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);

        Assert.Equal("-a", prefix[4]);
        Assert.Equal(extra, prefix[5]);
        Assert.Equal("--", prefix[6]);
    }

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            1, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>
            { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2);
}
