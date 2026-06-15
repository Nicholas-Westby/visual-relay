using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

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

    [Fact]
    public async Task TryLoadAsync_NoFile_ReturnsDefaulted()
    {
        using var repo = TestRepository.Create();
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Defaulted, result.Status);
        Assert.Equal("llm-tasks", result.Config.TasksDir);
    }

    [Fact]
    public async Task TryLoadAsync_OmittedLogSources_DefaultsToEmptyAndLoads()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test" }""");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Empty(result.Config.LogSources);
    }

    [Fact]
    public async Task TryLoadAsync_MissingTestCmd_ReturnsIncomplete()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"), """{ "logSources": [] }""");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task TryLoadAsync_LogSourcesWrongType_ReturnsMalformedWithDiagnostic()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": "logs/app.log" }""");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Malformed, result.Status);
        Assert.Contains("logSources must be an array", result.Diagnostic);
    }

    [Fact]
    public async Task TryLoadAsync_BlankTestCmd_ReturnsIncomplete()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"), """{ "testCmd": "   " }""");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task TryLoadAsync_InvalidJson_ReturnsMalformed()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"), "{ not json");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Malformed, result.Status);
    }

    [Fact]
    public async Task LoadAsync_TestTimeoutMs_DefaultsTo300000()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [] }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        // When the JSON omits testTimeoutMs, the default 5-minute cap is used.
        Assert.Equal(300_000, config.TestTimeoutMilliseconds);
    }

    [Fact]
    public async Task TryLoadAsync_TestTimeoutMs_Override()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "testTimeoutMs": 600000 }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(600_000, result.Config.TestTimeoutMilliseconds);
    }

    [Fact]
    public async Task LoadAsync_BypassSandboxDefaultsToFalse()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [] }""");

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        // Default is false: nono sandbox is required. Sandbox-2 made nono a hard prerequisite.
        Assert.False(config.BypassSandbox);
    }

    [Fact]
    public async Task TryLoadAsync_BypassSandboxTrue_FlipsFlag()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "bypassSandbox": true }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.True(result.Config.BypassSandbox);
    }

    [Fact]
    public void Defaults_TierProfiles_ContainsFallbackMappedToFallback()
    {
        var defaults = RelayConfigLoader.Defaults();
        Assert.True(defaults.TierProfiles.ContainsKey("fallback"));
        Assert.Equal("fallback", defaults.TierProfiles["fallback"]);
    }

    [Fact]
    public async Task LoadAsync_TierProfilesFallbackOverride_ReplacesDefault()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "tierProfiles": { "fallback": "custom-hf-model" }
            }
            """);

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        // The fallback tier should be overridden by the config file.
        Assert.Equal("custom-hf-model", config.TierProfiles["fallback"]);

        // Other tiers should retain their defaults.
        Assert.Equal("balanced", config.TierProfiles["balanced"]);
        Assert.Equal("cheap", config.TierProfiles["cheap"]);
        Assert.Equal("frontier", config.TierProfiles["frontier"]);
    }

    [Fact]
    public async Task TryLoadAsync_TierProfilesFallbackOverride_ReturnsLoaded()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "tierProfiles": { "fallback": "my-fallback-model" }
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("my-fallback-model", result.Config.TierProfiles["fallback"]);
        // Verify the override didn't clobber other defaults.
        Assert.Equal("frontier", result.Config.TierProfiles["frontier"]);
    }

    // ── First-output timeout watchdog config ──────────────────────────

    [Fact]
    public void Defaults_FirstOutputTimeoutMsByTier_HasPerTierDefaults()
    {
        var defaults = RelayConfigLoader.Defaults();

        Assert.Equal(90_000, defaults.FirstOutputTimeoutMsByTier["cheap"]);
        Assert.Equal(120_000, defaults.FirstOutputTimeoutMsByTier["balanced"]);
        Assert.Equal(660_000, defaults.FirstOutputTimeoutMsByTier["frontier"]);
        Assert.Equal(660_000, defaults.FirstOutputTimeoutMs);
        Assert.Equal(2, defaults.MaxStallRetries);
    }

    [Fact]
    public async Task LoadAsync_FirstOutputTimeoutMsByTier_Override()
    {
        // JSON override merges with defaults: cheap is overridden, vision is
        // added, balanced and frontier retain their defaults.
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "firstOutputTimeoutMsByTier": { "cheap": 45000, "vision": 120000 }
            }
            """);

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.Equal(45_000, config.FirstOutputTimeoutMsByTier["cheap"]);
        Assert.Equal(120_000, config.FirstOutputTimeoutMsByTier["vision"]);
        // Unmentioned tiers keep their defaults.
        Assert.Equal(120_000, config.FirstOutputTimeoutMsByTier["balanced"]);
        Assert.Equal(660_000, config.FirstOutputTimeoutMsByTier["frontier"]);
    }

    [Fact]
    public async Task TryLoadAsync_FirstOutputTimeoutMs_ScalarOverride()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """
            {
              "testCmd": "dotnet test",
              "firstOutputTimeoutMs": 300000,
              "maxStallRetries": 3
            }
            """);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(300_000, result.Config.FirstOutputTimeoutMs);
        Assert.Equal(3, result.Config.MaxStallRetries);
        // Absent per-tier map retains defaults.
        Assert.Equal(90_000, result.Config.FirstOutputTimeoutMsByTier["cheap"]);
    }

    [Fact]
    public void DownshiftOnEarlyImplementation_DefaultsToTrue()
    {
        Assert.True(RelayConfigLoader.Defaults().DownshiftOnEarlyImplementation);
    }

    [Fact]
    public async Task TryLoadAsync_DownshiftOnEarlyImplementation_False_OverridesDefault()
    {
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "downshiftOnEarlyImplementation": false }""");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.False(result.Config.DownshiftOnEarlyImplementation);
    }
}
