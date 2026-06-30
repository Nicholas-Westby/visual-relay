using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayConfigLoaderTests
{
    // ── Tier model overrides ──────────────────────────────────────────────

    [Fact]
    public async Task TierModelOverrides_LoadedFromConfig()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "tierModelOverrides": { "cheap": "gpt-5", "frontier": "claude-opus-1m" }
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.NotNull(result.Config.TierModelOverrides);
        Assert.Equal("gpt-5", result.Config.TierModelOverrides!["cheap"]);
        Assert.Equal("claude-opus-1m", result.Config.TierModelOverrides!["frontier"]);
    }

    [Fact]
    public async Task TierModelOverrides_InvalidEntryDropped()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "tierModelOverrides": { "cheap": "not-a-real-model", "frontier": "glm-5.2" }
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.NotNull(result.Config.TierModelOverrides);
        Assert.False(result.Config.TierModelOverrides!.ContainsKey("cheap"));
        Assert.Equal("glm-5.2", result.Config.TierModelOverrides!["frontier"]);
    }

    [Fact]
    public async Task TierModelOverrides_MissingKey_DefaultsToNull()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test" }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Null(result.Config.TierModelOverrides);
    }

    [Fact]
    public async Task TierModelOverrides_EmptyObject_DefaultsToNull()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "tierModelOverrides": {} }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Null(result.Config.TierModelOverrides);
    }

    [Fact]
    public async Task TierModelOverrides_OtherKeysPreserved()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "baselineVerify": false,
              "tierModelOverrides": { "cheap": "deepseek-v4-pro" }
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("dotnet test", result.Config.TestCommand);
        Assert.False(result.Config.BaselineVerify);
        Assert.NotNull(result.Config.TierModelOverrides);
        Assert.Equal("deepseek-v4-pro", result.Config.TierModelOverrides!["cheap"]);
    }
}
