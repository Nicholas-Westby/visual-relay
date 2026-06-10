using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayConfigWriterTests
{
    [Fact]
    public async Task Write_WithCommand_ProducesLoadableConfig()
    {
        using var repo = TestRepository.Create();
        var path = RelayConfigWriter.Write(repo.Root, "dotnet test");
        Assert.True(File.Exists(path));

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("dotnet test", result.Config.TestCommand);
        Assert.Empty(result.Config.LogSources);
    }

    [Fact]
    public async Task Write_WithEmptyCommand_ProducesIncompleteConfig()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, string.Empty);
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task UpsertBypassSandbox_True_RoundTripsThroughLoader()
    {
        using var repo = TestRepository.Create();
        // First write a config with Write so it's loadable (upsert needs
        // something to read-modify-write; a missing file is fine too).
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        RelayConfigWriter.UpsertBypassSandbox(repo.Root, true);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.True(result.Config.BypassSandbox);
    }

    [Fact]
    public async Task UpsertBypassSandbox_PreservesExistingKeys()
    {
        using var repo = TestRepository.Create();
        // Use WriteConfig to seed a config with tierProfiles and baselineVerify.
        repo.WriteConfig("dotnet test", [], baselineVerify: true);
        var configPath = Path.Combine(repo.Root, ".relay", "config.json");

        // Sanity: the seeded config is loadable and BypassSandbox defaults to false.
        var before = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, before.Status);
        Assert.False(before.Config.BypassSandbox);
        Assert.True(before.Config.BaselineVerify);
        Assert.Contains("cheap", before.Config.TierProfiles);

        RelayConfigWriter.UpsertBypassSandbox(repo.Root, true);

        var after = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, after.Status);
        Assert.True(after.Config.BypassSandbox);
        // Existing keys must survive the upsert.
        Assert.True(after.Config.BaselineVerify);
        Assert.Contains("cheap", after.Config.TierProfiles);
        Assert.Equal("dotnet test", after.Config.TestCommand);
        Assert.Empty(after.Config.LogSources);
    }
}
