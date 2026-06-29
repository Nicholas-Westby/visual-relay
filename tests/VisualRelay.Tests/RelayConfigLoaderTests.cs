using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
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
              "enableFixVerify": false,
              "tierProfiles": { "cheap": "local-cheap" }
            }
            """);

        var config = await RelayConfigLoader.LoadAsync(repo.Root);

        Assert.Equal("llm-tasks", config.TasksDir);
        Assert.Equal("dotnet test", config.TestCommand);
        Assert.Equal("bun test {files}", config.TestFileCommand);
        Assert.Equal(["logs/app.log"], config.LogSources);
        Assert.False(config.EnableFixVerify);
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
    public async Task LoadAsync_StaleBypassSandboxKey_LoadsAndStaysSandboxed()
    {
        // Regression: the bypassSandbox capability was removed. A stale
        // "bypassSandbox": true left in an old config must be SILENTLY IGNORED
        // (parsed-and-dropped, not a load error) — and the run is still sandboxed,
        // so nono remains an unconditional prerequisite. Against the old code this
        // failed: the key was honoured and nono was treated as optional.
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "bypassSandbox": true }""");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        // The stale key does not break the load.
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);

        // And the sandbox is still effectively on: nono is reported missing on a
        // PATH that lacks it (swival present so only nono is flagged), proving the
        // stale opt-out does not make nono optional.
        var stubBin = Path.Combine(repo.Root, "bin");
        Directory.CreateDirectory(stubBin);
        await File.WriteAllTextAsync(Path.Combine(stubBin, "swival"), "#!/bin/sh\nexit 0\n");
        var missing = SwivalSubagentRunner.MissingRequiredTools(result.Config, pathValue: stubBin);
        Assert.Contains("nono", missing);
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
