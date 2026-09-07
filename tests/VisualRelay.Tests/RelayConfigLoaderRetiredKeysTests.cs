using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A key Visual Relay no longer honours must never turn a target's config into a
/// load error: repos in the wild still carry <c>commitProofArtifacts</c> from the
/// versions that force-added run artifacts into every task commit.
/// </summary>
public sealed class RelayConfigLoaderRetiredKeysTests
{
    [Fact]
    public async Task TryLoadAsync_ConfigCarryingRetiredCommitProofArtifactsKey_StillLoads()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "commitProofArtifacts": false }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("dotnet test", result.Config.TestCommand);
    }

    [Fact]
    public void RelayConfig_NoLongerExposesCommitProofArtifacts()
    {
        // The setting is gone, not merely ignored: nothing under .relay/ is ever
        // staged, so there is no longer a choice to offer.
        Assert.DoesNotContain(
            typeof(RelayConfig).GetProperties(),
            p => p.Name == "CommitProofArtifacts");
    }
}
