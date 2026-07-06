using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayConfigLoaderSubagentTimeoutTests
{
    [Fact]
    public async Task LoadAsync_SubagentTimeoutDefaultsTo45Minutes()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [] }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.Equal(2_700_000, config.SubagentTimeoutMilliseconds);
    }

    [Fact]
    public async Task TryLoadAsync_SubagentTimeoutExplicitValueWins()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "subagentTimeoutMs": 3600000 }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(3_600_000, result.Config.SubagentTimeoutMilliseconds);
    }
}
