using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// Loader tolerance for the retired retry-pool knobs. When stage retries became
// always-escalate, maxStallRetries / maxContractRetries were removed from the
// config surface. Stale configs that still carry them must load cleanly — the
// loader ignores unknown keys.
public sealed partial class RelayConfigLoaderTests
{
    [Fact]
    public async Task TryLoadAsync_StaleConfigWithRemovedRetryKeys_StillLoads()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "maxStallRetries": 3,
              "maxContractRetries": 2
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("dotnet test", result.Config.TestCommand);
    }
}
