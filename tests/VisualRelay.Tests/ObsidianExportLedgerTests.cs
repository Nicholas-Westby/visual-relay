using System.Text.Json;
using VisualRelay.Core.ObsidianBridge;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for <see cref="ExportLedger"/> — the per-repo export ledger that
/// records every task id whose summary has been exported to the Obsidian vault.
/// These tests exercise the ledger in isolation (plain filesystem, no Avalonia).
/// </summary>
public sealed class ObsidianExportLedgerTests : IDisposable
{
    private readonly string _tempDir;

    public ObsidianExportLedgerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "vr-export-ledger-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        TestFileSystem.DeleteDirectoryResilient(_tempDir);
    }

    // ── ContainsAsync / RecordAsync ────────────────────────────────────

    [Fact]
    public async Task ContainsAsync_UnknownTaskId_ReturnsFalse()
    {
        var ledger = new ExportLedger(_tempDir);
        var result = await ledger.ContainsAsync("some-task");
        Assert.False(result);
    }

    [Fact]
    public async Task RecordAsync_ThenContainsAsync_ReturnsTrue()
    {
        var ledger = new ExportLedger(_tempDir);
        await ledger.RecordAsync("task-a");
        Assert.True(await ledger.ContainsAsync("task-a"));
    }

    [Fact]
    public async Task RecordAsync_DoesNotAffectUnrelatedTask()
    {
        var ledger = new ExportLedger(_tempDir);
        await ledger.RecordAsync("task-a");
        Assert.False(await ledger.ContainsAsync("task-b"));
    }

    // ── RecordBatchAsync ───────────────────────────────────────────────

    [Fact]
    public async Task RecordBatchAsync_RecordsAllIds()
    {
        var ledger = new ExportLedger(_tempDir);
        await ledger.RecordBatchAsync(["alpha", "beta", "gamma"]);
        Assert.True(await ledger.ContainsAsync("alpha"));
        Assert.True(await ledger.ContainsAsync("beta"));
        Assert.True(await ledger.ContainsAsync("gamma"));
    }

    [Fact]
    public async Task RecordBatchAsync_EmptyList_DoesNotCorrupt()
    {
        var ledger = new ExportLedger(_tempDir);
        await ledger.RecordAsync("existing");
        await ledger.RecordBatchAsync([]);
        Assert.True(await ledger.ContainsAsync("existing"));
    }

    // ── Atomic write: no partial file ──────────────────────────────────

    [Fact]
    public async Task RecordAsync_WritesAtomically_NoPartialFileObserved()
    {
        // Verify the ledger file exists with valid JSON after a write — a
        // partial/temp file rename failure would leave no file or malformed JSON.
        var ledger = new ExportLedger(_tempDir);
        await ledger.RecordAsync("safe-write");

        var ledgerPath = Path.Combine(_tempDir, ".vr-export-ledger.json");
        Assert.True(File.Exists(ledgerPath));

        // Must be parseable JSON.
        var text = await File.ReadAllTextAsync(ledgerPath);
        using var doc = JsonDocument.Parse(text);
        Assert.NotNull(doc);
    }

    // ── TrySeedAsync: no ledger, no notes → FullBackfill ───────────────

    [Fact]
    public async Task TrySeedAsync_NoLedger_NoExistingNotes_ReturnsFullBackfill()
    {
        var ledger = new ExportLedger(_tempDir);
        var (decision, ids) = await ledger.TrySeedAsync(
            ["task-1", "task-2"], hasExistingNotes: false);

        Assert.Equal(SeedDecision.FullBackfill, decision);
        Assert.Contains("task-1", ids);
        Assert.Contains("task-2", ids);
    }

    // ── TrySeedAsync: no ledger, existing notes → SealOnly ─────────────

    [Fact]
    public async Task TrySeedAsync_NoLedger_HasExistingNotes_ReturnsSealOnly()
    {
        var ledger = new ExportLedger(_tempDir);
        var (decision, ids) = await ledger.TrySeedAsync(
            ["task-1", "task-2"], hasExistingNotes: true);

        Assert.Equal(SeedDecision.SealOnly, decision);
        Assert.Contains("task-1", ids);
        Assert.Contains("task-2", ids);
    }

    // ── TrySeedAsync: valid ledger → Skip ──────────────────────────────

    [Fact]
    public async Task TrySeedAsync_ValidLedgerExists_ReturnsSkip()
    {
        // Pre-populate a valid ledger so TrySeedAsync finds it.
        var ledgerPath = Path.Combine(_tempDir, ".vr-export-ledger.json");
        await File.WriteAllTextAsync(ledgerPath, """{"ids":["existing-task"]}""");

        var ledger = new ExportLedger(_tempDir);
        var (decision, ids) = await ledger.TrySeedAsync(
            ["task-1"], hasExistingNotes: false);

        Assert.Equal(SeedDecision.Skip, decision);
        Assert.Empty(ids);
    }

    // ── Corrupt ledger treated as absent ───────────────────────────────

    [Fact]
    public async Task TrySeedAsync_CorruptLedger_TreatedAsAbsent_NoCrash()
    {
        var ledgerPath = Path.Combine(_tempDir, ".vr-export-ledger.json");
        await File.WriteAllTextAsync(ledgerPath, "this is not valid json {{{");

        var ledger = new ExportLedger(_tempDir);
        // Must not throw.
        var (decision, ids) = await ledger.TrySeedAsync(
            ["task-1"], hasExistingNotes: false);

        // Corrupt ledger → absent → FullBackfill (no notes).
        Assert.Equal(SeedDecision.FullBackfill, decision);
        Assert.Contains("task-1", ids);
    }

    [Fact]
    public async Task ContainsAsync_CorruptLedger_TreatedAsEmpty()
    {
        var ledgerPath = Path.Combine(_tempDir, ".vr-export-ledger.json");
        await File.WriteAllTextAsync(ledgerPath, "garbage");

        var ledger = new ExportLedger(_tempDir);
        // Must not throw; corrupt ledger → empty set.
        var result = await ledger.ContainsAsync("any-task");
        Assert.False(result);
    }

    // ── Ledger persists across instances ───────────────────────────────

    [Fact]
    public async Task RecordAsync_PersistsAcrossInstances()
    {
        var ledger1 = new ExportLedger(_tempDir);
        await ledger1.RecordAsync("persistent");

        var ledger2 = new ExportLedger(_tempDir);
        Assert.True(await ledger2.ContainsAsync("persistent"));
    }
}
