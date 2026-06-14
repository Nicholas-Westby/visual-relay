using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayConfigLoaderReviewEscalationTests
{
    [Fact]
    public void Defaults_ReviewEscalation_HasCorrectDefaults()
    {
        var defaults = RelayConfigLoader.Defaults();

        Assert.True(defaults.ReviewEscalationEnabled);
        Assert.Equal(10, defaults.ReviewEscalationManifestFileThreshold);
        Assert.Equal(500, defaults.ReviewEscalationManifestLineThreshold);
    }

    [Fact]
    public async Task LoadAsync_ReviewEscalationDisabled_ParsesFalse()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "logSources": [],
              "reviewEscalationEnabled": false
            }
            """);

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.False(config.ReviewEscalationEnabled);
        // Thresholds retain defaults when absent.
        Assert.Equal(10, config.ReviewEscalationManifestFileThreshold);
        Assert.Equal(500, config.ReviewEscalationManifestLineThreshold);
    }

    [Fact]
    public async Task LoadAsync_ReviewEscalationCustomThresholds_ParsesValues()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "logSources": [],
              "reviewEscalationManifestFileThreshold": 5,
              "reviewEscalationManifestLineThreshold": 200
            }
            """);

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.True(config.ReviewEscalationEnabled); // default when absent
        Assert.Equal(5, config.ReviewEscalationManifestFileThreshold);
        Assert.Equal(200, config.ReviewEscalationManifestLineThreshold);
    }

    [Fact]
    public async Task LoadAsync_ReviewEscalationOmitted_DefaultsApply()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [] }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.True(config.ReviewEscalationEnabled);
        Assert.Equal(10, config.ReviewEscalationManifestFileThreshold);
        Assert.Equal(500, config.ReviewEscalationManifestLineThreshold);
    }
}
