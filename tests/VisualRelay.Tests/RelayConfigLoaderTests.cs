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
}
