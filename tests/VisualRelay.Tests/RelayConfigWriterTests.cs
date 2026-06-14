using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class RelayConfigWriterTests
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
        // (c) Exhaustion path: when no candidate can be validated, the
        // writer receives null and must produce a config whose testCmd is
        // null — which the loader treats as Incomplete (not a bad guess).
        using var repo = TestRepository.Create();
        var path = RelayConfigWriter.Write(repo.Root, null);
        Assert.True(File.Exists(path));

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Incomplete, result.Status);
    }

    [Fact]
    public async Task Write_WithNullTestCmd_JsonContainsNullValue()
    {
        // The on-disk JSON must have a null value (not a missing key) so
        // the file is self-documenting: the user can see testCmd was
        // explicitly set to null because detection failed.
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
        // (d) The validated command must survive the write → load
        // round-trip exactly as provided — no trimming, no shell wrapping.
        using var repo = TestRepository.Create();
        var original = "dotnet test --filter Category=Unit --logger trx";

        RelayConfigWriter.Write(repo.Root, original);
        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);

        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.Equal(original, result.Config.TestCommand);
    }

    // ── UpsertBypassSandbox (existing, preserved) ───────────────────────

    [Fact]
    public async Task UpsertBypassSandbox_True_RoundTripsThroughLoader()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");

        RelayConfigWriter.UpsertBypassSandbox(repo.Root, true);

        var result = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, result.Status);
        Assert.True(result.Config.BypassSandbox);
    }

    [Fact]
    public async Task UpsertBypassSandbox_PreservesExistingKeys()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", [], baselineVerify: true);

        var before = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, before.Status);
        Assert.False(before.Config.BypassSandbox);
        Assert.True(before.Config.BaselineVerify);
        Assert.Contains("cheap", before.Config.TierProfiles);

        RelayConfigWriter.UpsertBypassSandbox(repo.Root, true);

        var after = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, after.Status);
        Assert.True(after.Config.BypassSandbox);
        Assert.True(after.Config.BaselineVerify);
        Assert.Contains("cheap", after.Config.TierProfiles);
        Assert.Equal("dotnet test", after.Config.TestCommand);
        Assert.Empty(after.Config.LogSources);
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
}
