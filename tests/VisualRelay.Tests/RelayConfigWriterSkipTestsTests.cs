using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayConfigWriterTests
{
    // ── SetSkipTests ─────────────────────────────────────────────────────

    [Fact]
    public async Task SetSkipTests_adds_taskId()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        RelayConfigWriter.SetSkipTests(repo.Root, "readme-only", enabled: true);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Contains("readme-only", result.Config.SkipTestsTaskIds!);
    }

    [Fact]
    public async Task SetSkipTests_adds_idempotent()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        RelayConfigWriter.SetSkipTests(repo.Root, "readme-only", enabled: true);
        RelayConfigWriter.SetSkipTests(repo.Root, "readme-only", enabled: true);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Single(result.Config.SkipTestsTaskIds!, id => id == "readme-only");
    }

    [Fact]
    public async Task SetSkipTests_removes_taskId()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        RelayConfigWriter.SetSkipTests(repo.Root, "readme-only", enabled: true);
        RelayConfigWriter.SetSkipTests(repo.Root, "readme-only", enabled: false);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.DoesNotContain("readme-only", result.Config.SkipTestsTaskIds!);
    }

    [Fact]
    public async Task SetSkipTests_preserves_all_other_keys()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: true);

        var before = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, before.Status);
        Assert.Empty(before.Config.SkipTestsTaskIds!);
        Assert.True(before.Config.BaselineVerify);
        Assert.Contains("cheap", before.Config.TierProfiles);
        Assert.Equal("dotnet test", before.Config.TestCommand);
        Assert.Empty(before.Config.LogSources);

        RelayConfigWriter.SetSkipTests(repo.Root, "readme-only", enabled: true);

        var after = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, after.Status);
        Assert.Contains("readme-only", after.Config.SkipTestsTaskIds!);
        Assert.True(after.Config.BaselineVerify);
        Assert.Contains("cheap", after.Config.TierProfiles);
        Assert.Equal("dotnet test", after.Config.TestCommand);
        Assert.Empty(after.Config.LogSources);
    }
}
