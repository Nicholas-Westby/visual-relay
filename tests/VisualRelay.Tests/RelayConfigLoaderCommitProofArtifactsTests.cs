using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayConfigLoaderCommitProofArtifactsTests
{
    [Fact]
    public async Task LoadAsync_CommitProofArtifactsDefaultsToTrue()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [] }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        // Default is true: proof files are force-committed (current behavior).
        Assert.True(config.CommitProofArtifacts);
    }

    [Fact]
    public async Task TryLoadAsync_CommitProofArtifactsFalse_Honored()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "commitProofArtifacts": false }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.False(result.Config.CommitProofArtifacts);
    }
}
