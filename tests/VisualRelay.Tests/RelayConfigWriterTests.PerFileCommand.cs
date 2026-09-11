using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Companion to <see cref="RelayConfigWriterTests"/> — what bootstrap seeds as the
/// per-file test command. Copying the whole-suite command into it made the stage-5
/// gate report a targeted run that was in fact the entire suite.
/// </summary>
public sealed partial class RelayConfigWriterTests
{
    private static string? WrittenTestFileCommand(string root)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, ".relay", "config.json")));
        return document.RootElement.TryGetProperty("testFileCmd", out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    [Theory]
    [InlineData("cargo test")]
    [InlineData("go test ./...")]
    [InlineData("dotnet test")]
    public async Task Write_AToolchainWithNoPerFileForm_LeavesTestFileCmdNull(string command)
    {
        using var repo = TestRepository.Create();

        RelayConfigWriter.Write(repo.Root, command);

        Assert.Null(WrittenTestFileCommand(repo.Root));
        // Null is the deliberate signal: the loader then falls back to testCmd, so
        // the gate runs the suite and says so rather than claiming a narrow run.
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(command, result.Config.TestFileCommand);
    }

    [Theory]
    [InlineData("pytest", "pytest {files}")]
    [InlineData(".venv/bin/pytest -q", ".venv/bin/pytest -q {files}")]
    [InlineData("bun test", "bun test {files}")]
    public async Task Write_AToolchainWithAPerFileForm_SeedsIt(string command, string expected)
    {
        using var repo = TestRepository.Create();

        RelayConfigWriter.Write(repo.Root, command);

        Assert.Equal(expected, WrittenTestFileCommand(repo.Root));
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(expected, result.Config.TestFileCommand);
    }

    [Fact]
    public async Task UpsertResolvedToolchain_AToolchainWithNoPerFileForm_ClearsThePlaceholder()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, ProjectBootstrapper.PlaceholderTestCommand);

        RelayConfigWriter.UpsertResolvedToolchain(repo.Root, "cargo test");

        Assert.Null(WrittenTestFileCommand(repo.Root));
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal("cargo test", result.Config.TestCommand);
        Assert.Equal("cargo test", result.Config.TestFileCommand);
    }
}
