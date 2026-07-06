using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayConfigWriterTests
{
    [Fact]
    public async Task Write_WithCommand_ProducesLoadableConfig()
    {
        using var repo = TestRepository.Create();
        var path = RelayConfigWriter.Write(repo.Root, "dotnet test");
        Assert.True(File.Exists(path));

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("dotnet test", result.Config.TestCommand);
        Assert.Empty(result.Config.LogSources);
    }

    [Fact]
    public async Task Write_WithEmptyCommand_ProducesIncompleteConfig()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, string.Empty);
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task Write_WithNullTestCmd_ProducesIncompleteConfig()
    {
        // Exhaustion path: null testCmd → loader returns Incomplete (not a bad guess).
        using var repo = TestRepository.Create();
        var path = RelayConfigWriter.Write(repo.Root, null);
        Assert.True(File.Exists(path));

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task Write_WithNullTestCmd_JsonContainsNullValue()
    {
        // On-disk JSON must have null (not missing key) — self-documenting exhaustion.
        using var repo = TestRepository.Create();
        var path = RelayConfigWriter.Write(repo.Root, null);

        var raw = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(raw);

        Assert.True(doc.RootElement.TryGetProperty("testCmd", out var testCmd));
        Assert.Equal(JsonValueKind.Null, testCmd.ValueKind);
    }

    // ── Validate-then-write: validated command written verbatim ─────────

    [Fact]
    public async Task Write_ThenLoad_RoundTripsCommandVerbatim()
    {
        // Validated command survives write→load round-trip exactly — no trimming, no shell wrapping.
        using var repo = TestRepository.Create();
        var original = "dotnet test --filter Category=Unit --logger trx";

        RelayConfigWriter.Write(repo.Root, original);
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(original, result.Config.TestCommand);
    }

    // ── testFileCmd consistency (no orphaned "bun test {files}" default) ──

    [Fact]
    public async Task Write_WithPlaceholderTestCmd_TestFileCommandIsPlaceholder_NotBunDefault()
    {
        // testFileCmd tracks placeholder (not "bun test {files}" default) so the stage
        // agent never infers Bun and writes .test.ts junk. No {files} token → clean fallback.
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, ProjectBootstrapper.PlaceholderTestCommand);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.DoesNotContain("bun", result.Config.TestFileCommand);
        Assert.Equal(ProjectBootstrapper.PlaceholderTestCommand, result.Config.TestFileCommand);
    }

    [Fact]
    public async Task Write_WithRealTestCmd_TestFileCommandIsConsistent_NotBunDefault()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "go test ./...");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.DoesNotContain("bun", result.Config.TestFileCommand);
        Assert.Equal("go test ./...", result.Config.TestFileCommand);
    }

    [Fact]
    public async Task UpsertResolvedToolchain_FromPlaceholder_RewritesTestFileCommandConsistently()
    {
        // Placeholder→real upgrade must also replace placeholder testFileCmd with consistent one.
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, ProjectBootstrapper.PlaceholderTestCommand);

        RelayConfigWriter.UpsertResolvedToolchain(repo.Root, "pytest");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("pytest", result.Config.TestCommand);
        Assert.DoesNotContain("bun", result.Config.TestFileCommand);
        Assert.Equal("pytest", result.Config.TestFileCommand);
    }

    // ── Swift guard detection ───────────────────────────────────────────

    [Fact]
    public async Task Write_SwiftPackage_ProducesSwiftBuildGuard()
    {
        using var repo = TestRepository.Create();
        File.WriteAllText(Path.Combine(repo.Root, "Package.swift"), "// swift-tools-version:5.9");

        RelayConfigWriter.Write(repo.Root, "swift test");

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal("swift test", result.Config.TestCommand);
        Assert.Equal("swift build", result.Config.GuardCommand);
    }

    [Fact]
    public async Task Write_NoGuardsNoToolchainMarker_GuardCommandIsNull()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "echo test");
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Null(result.Config.GuardCommand);
    }

    // ── UpsertCommitProofArtifacts ──────────────────────────────────────

    [Fact]
    public async Task UpsertCommitProofArtifacts_False_RoundTripsThroughLoader()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        RelayConfigWriter.UpsertCommitProofArtifacts(repo.Root, false);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.False(result.Config.CommitProofArtifacts);
    }

    [Fact]
    public async Task UpsertCommitProofArtifacts_PreservesExistingKeys()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: true);

        var before = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, before.Status);
        Assert.True(before.Config.CommitProofArtifacts); // default
        Assert.True(before.Config.BaselineVerify);
        Assert.Contains("cheap", before.Config.TierProfiles);

        RelayConfigWriter.UpsertCommitProofArtifacts(repo.Root, false);

        var after = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, after.Status);
        Assert.False(after.Config.CommitProofArtifacts);
        Assert.True(after.Config.BaselineVerify);
        Assert.Contains("cheap", after.Config.TierProfiles);
        Assert.Equal("dotnet test", after.Config.TestCommand);
        Assert.Empty(after.Config.LogSources);
    }

    // ── UpsertSubagentTimeout ──────────────────────────────────────────

    [Fact]
    public async Task UpsertSubagentTimeout_SetsValue()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        RelayConfigWriter.UpsertSubagentTimeout(repo.Root, 2_400_000);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(2_400_000, result.Config.SubagentTimeoutMilliseconds);
    }

    [Fact]
    public async Task UpsertSubagentTimeout_PreservesOtherKeys()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: true);

        var before = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, before.Status);
        Assert.True(before.Config.BaselineVerify);
        Assert.Contains("cheap", before.Config.TierProfiles);
        Assert.Equal("dotnet test", before.Config.TestCommand);

        RelayConfigWriter.UpsertSubagentTimeout(repo.Root, 2_400_000);

        var after = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, after.Status);
        Assert.Equal(2_400_000, after.Config.SubagentTimeoutMilliseconds);
        Assert.True(after.Config.BaselineVerify);
        Assert.Contains("cheap", after.Config.TierProfiles);
        Assert.Equal("dotnet test", after.Config.TestCommand);
        Assert.Empty(after.Config.LogSources);
    }

    [Fact]
    public async Task UpsertSubagentTimeout_CreatesKeyWhenAbsent()
    {
        using var repo = TestRepository.Create();
        // Write a config without subagentTimeoutMs — the loader defaults to 2_700_000.
        repo.WriteConfig("dotnet test", []);

        RelayConfigWriter.UpsertSubagentTimeout(repo.Root, 2_400_000);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(2_400_000, result.Config.SubagentTimeoutMilliseconds);
        Assert.Equal("dotnet test", result.Config.TestCommand);
    }

    // ── SetTurnBoost ────────────────────────────────────────────────────

    [Fact]
    public async Task SetTurnBoost_adds_taskId()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        RelayConfigWriter.SetTurnBoost(repo.Root, "big-task", enabled: true);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Contains("big-task", result.Config.BoostTurnsTaskIds!);
    }

    [Fact]
    public async Task SetTurnBoost_adds_idempotent()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        RelayConfigWriter.SetTurnBoost(repo.Root, "big-task", enabled: true);
        RelayConfigWriter.SetTurnBoost(repo.Root, "big-task", enabled: true);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        // Should only appear once (de-duplicated).
        Assert.Single(result.Config.BoostTurnsTaskIds!, id => id == "big-task");
    }

    [Fact]
    public async Task SetTurnBoost_removes_taskId()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        RelayConfigWriter.SetTurnBoost(repo.Root, "big-task", enabled: true);
        RelayConfigWriter.SetTurnBoost(repo.Root, "big-task", enabled: false);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.DoesNotContain("big-task", result.Config.BoostTurnsTaskIds!);
    }

    [Fact]
    public async Task SetTurnBoost_preserves_all_other_keys()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: true);

        var before = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, before.Status);
        Assert.Empty(before.Config.BoostTurnsTaskIds!);
        Assert.True(before.Config.BaselineVerify);
        Assert.Contains("cheap", before.Config.TierProfiles);
        Assert.Equal("dotnet test", before.Config.TestCommand);
        Assert.Empty(before.Config.LogSources);

        RelayConfigWriter.SetTurnBoost(repo.Root, "huge-task", enabled: true);

        var after = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, after.Status);
        Assert.Contains("huge-task", after.Config.BoostTurnsTaskIds!);
        Assert.True(after.Config.BaselineVerify);
        Assert.Contains("cheap", after.Config.TierProfiles);
        Assert.Equal("dotnet test", after.Config.TestCommand);
        Assert.Empty(after.Config.LogSources);
    }
}
