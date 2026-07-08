namespace VisualRelay.Tests;

/// <summary>
/// Guard tests that verify the xunit.runner.json configuration matches the
/// oversubscription policy needed to speed up the wait-dominated test suite.
/// These tests assert the TARGET state — they fail until the config is updated.
/// </summary>
public sealed class XunitRunnerConfigTests
{
    private static string ConfigPath =>
        Path.Combine(RepoSetup.Root, "tests", "VisualRelay.Tests", "xunit.runner.json");

    [Fact]
    public void XunitRunnerJson_HasMaxParallelThreads_Oversubscription()
    {
        Assert.True(File.Exists(ConfigPath), $"Missing: {ConfigPath}");
        var content = File.ReadAllText(ConfigPath);

        Assert.Contains("\"maxParallelThreads\"", content, StringComparison.Ordinal);
        Assert.Contains("\"2.0x\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void XunitRunnerJson_HasParallelAlgorithm_Aggressive()
    {
        Assert.True(File.Exists(ConfigPath), $"Missing: {ConfigPath}");
        var content = File.ReadAllText(ConfigPath);

        Assert.Contains("\"parallelAlgorithm\"", content, StringComparison.Ordinal);
        Assert.Contains("\"aggressive\"", content, StringComparison.Ordinal);
    }
}
