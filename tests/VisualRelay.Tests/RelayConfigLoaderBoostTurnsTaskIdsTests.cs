using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayConfigLoaderBoostTurnsTaskIdsTests
{
    [Fact]
    public async Task absent_defaults_to_empty()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [] }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Empty(result.Config.BoostTurnsTaskIds!);
    }

    [Fact]
    public async Task present_populates_list()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "boostTurnsTaskIds": ["task-a", "task-b"] }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(["task-a", "task-b"], result.Config.BoostTurnsTaskIds);
    }

    [Fact]
    public async Task non_array_defaults_to_empty()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "boostTurnsTaskIds": "not-an-array" }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Empty(result.Config.BoostTurnsTaskIds!);
    }
}
