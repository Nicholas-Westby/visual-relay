using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayConfigWriterTests
{
    // ── UpsertTierModelOverrides ──────────────────────────────────────────

    [Fact]
    public async Task UpsertTierModelOverrides_WritesAndSurvivesRoundTrip()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        var overrides = new Dictionary<string, string>
        {
            ["cheap"] = "gpt-5",
            ["frontier"] = "claude-opus-1m",
        };
        RelayConfigWriter.UpsertTierModelOverrides(repo.Root, overrides);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.NotNull(result.Config.TierModelOverrides);
        Assert.Equal("gpt-5", result.Config.TierModelOverrides!["cheap"]);
        Assert.Equal("claude-opus-1m", result.Config.TierModelOverrides!["frontier"]);
    }

    [Fact]
    public async Task UpsertTierModelOverrides_PreservesExistingKeys()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: true);

        var before = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, before.Status);
        Assert.True(before.Config.BaselineVerify);

        RelayConfigWriter.UpsertTierModelOverrides(repo.Root,
            new Dictionary<string, string> { ["cheap"] = "deepseek-v4-pro" });

        var after = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, after.Status);
        Assert.True(after.Config.BaselineVerify);
        Assert.Equal("dotnet test", after.Config.TestCommand);
        Assert.NotNull(after.Config.TierModelOverrides);
        Assert.Equal("deepseek-v4-pro", after.Config.TierModelOverrides!["cheap"]);
    }
}
