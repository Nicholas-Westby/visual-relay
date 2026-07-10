using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for testFileCmd fallback behaviour: when testFileCmd is absent from JSON,
/// the loaded config should fall back to testCmd (not the old "bun test {files}"
/// default). An explicit testFileCmd value must be honoured verbatim.
/// </summary>
public sealed class RelayConfigLoaderTestFileCmdTests
{
    [Fact]
    public async Task TryLoadAsync_NoTestFileCmd_FallsBackToTestCommand()
    {
        // When testFileCmd is absent from JSON, the loaded config must
        // fall back to testCmd — NOT the old "bun test {files}" default.
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "logSources": [] }""");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("dotnet test", result.Config.TestFileCommand);
    }

    [Fact]
    public async Task TryLoadAsync_ExplicitTestFileCmd_HonoredVerbatim()
    {
        // When testFileCmd is explicitly set in JSON, it must be used as-is.
        using var repo = TestRepository.Create();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root, ".relay", "config.json"),
            """{ "testCmd": "dotnet test", "testFileCmd": "bun test {files}", "logSources": [] }""");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("bun test {files}", result.Config.TestFileCommand);
    }
}
