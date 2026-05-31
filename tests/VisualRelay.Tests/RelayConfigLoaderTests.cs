using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed class RelayConfigLoaderTests
{
    [Fact]
    public async Task LoadAsync_MergesRepositoryConfigWithRelayDefaults()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "logSources": ["logs/app.log"],
              "maxVerifyLoops": 2,
              "tierProfiles": { "cheap": "local-cheap" }
            }
            """);

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.Equal("llm-tasks", config.TasksDir);
        Assert.Equal("dotnet test", config.TestCommand);
        Assert.Equal("bun test {files}", config.TestFileCommand);
        Assert.Equal(["logs/app.log"], config.LogSources);
        Assert.Equal(2, config.MaxVerifyLoops);
        Assert.True(config.BaselineVerify);
        Assert.True(config.ArchiveOnDone);
        Assert.Equal("local-cheap", config.TierProfiles["cheap"]);
        Assert.Equal("balanced", config.TierProfiles["balanced"]);
    }
}
