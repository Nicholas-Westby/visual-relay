using VisualRelay.Core.ObsidianBridge;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Egress path-traversal guard for <see cref="ObsidianSummaryWriter"/>. Task ids
/// come from on-disk folder/file names (RelayTaskRepository), so a crafted id
/// (<c>..</c>, <c>a/b</c>, <c>x.y</c>, leading-dash, empty) must be rejected at the
/// export boundary BEFORE any path is composed — a rejected id is a logged no-op,
/// never a write outside <c>Completed/&lt;date&gt;/</c> and never a crash. A valid
/// slug still writes normally.
/// </summary>
public sealed class ObsidianSummaryWriterTraversalTests
{
    private static (string RepoRoot, ObsidianVaultLayout Layout, string VaultRoot) Setup()
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "vr-obsidian-traversal-vault",
            Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(Path.GetTempPath(), "vr-obsidian-traversal-repo",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(Path.Combine(repoRoot, "llm-tasks"));

        var layout = new ObsidianVaultLayout(vaultRoot, "test-repo");
        layout.EnsureScaffold();
        return (repoRoot, layout, vaultRoot);
    }

    private static void Cleanup(string repoRoot, ObsidianVaultLayout layout, string vaultRoot)
    {
        TestFileSystem.DeleteDirectoryResilient(repoRoot);
        TestFileSystem.DeleteDirectoryResilient(vaultRoot);
        TestFileSystem.DeleteDirectoryResilient(layout.RepoDir);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("x.y")]
    [InlineData("-leading")]
    [InlineData("")]
    [InlineData("../../escape")]
    public void Write_RejectsUnsafeTaskId_NoWriteOutsideCompleted(string badId)
    {
        var (repoRoot, layout, vaultRoot) = Setup();
        try
        {
            // Snapshot the entire vault tree before the (rejected) write.
            var before = Directory.GetFileSystemEntries(
                vaultRoot, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray();

            var outcome = new RelayTaskOutcome(
                badId, RelayTaskOutcomeStatus.Committed, "h", "deadbeef", null);
            var writer = new ObsidianSummaryWriter();

            // Must NOT throw — a rejected id is a best-effort no-op.
            writer.Write(layout, repoRoot, badId, outcome, "# spec\n\n.", null,
                new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));

            // Nothing new anywhere under the vault (no escape, no Completed write).
            var after = Directory.GetFileSystemEntries(
                vaultRoot, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray();
            Assert.Equal(before, after);
        }
        finally { Cleanup(repoRoot, layout, vaultRoot); }
    }

    [Fact]
    public void Write_RejectsDotDotId_DoesNotClobberSiblingFileAboveCompletedDate()
    {
        var (repoRoot, layout, vaultRoot) = Setup();
        try
        {
            // A victim file directly under Completed/ (where ".." from Completed/<date>/ lands).
            var completedRoot = Path.Combine(layout.RepoDir, "Completed");
            var victim = Path.Combine(completedRoot, "..md"); // SummaryPath("..", date) => Completed/<date>/...md — escapes via Combine
            var sentinel = Path.Combine(completedRoot, "victim.md");
            File.WriteAllText(sentinel, "ORIGINAL");

            var outcome = new RelayTaskOutcome(
                "..", RelayTaskOutcomeStatus.Committed, "h", "sha", null);
            new ObsidianSummaryWriter().Write(
                layout, repoRoot, "..", outcome, "# pwned\n\n.", null,
                new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));

            Assert.Equal("ORIGINAL", File.ReadAllText(sentinel));
            Assert.False(File.Exists(victim));
        }
        finally { Cleanup(repoRoot, layout, vaultRoot); }
    }

    [Fact]
    public void Build_RejectsUnsafeTaskId_ReturnsEmpty_DoesNotReadRelayParent()
    {
        var (repoRoot, layout, vaultRoot) = Setup();
        try
        {
            // Build composes Path.Combine(rootPath, ".relay", taskId); "../.." must not be honoured.
            var markdown = new ObsidianSummaryWriter().Build(
                repoRoot, "..", null, "# spec\n\n.", null,
                new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));

            Assert.Equal(string.Empty, markdown);
        }
        finally { Cleanup(repoRoot, layout, vaultRoot); }
    }

    [Fact]
    public void Write_ValidSlug_StillWrites()
    {
        var (repoRoot, layout, vaultRoot) = Setup();
        try
        {
            var taskPath = Path.Combine(repoRoot, "llm-tasks", "good-task", "good-task.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# Good Task\n\n.");

            var outcome = new RelayTaskOutcome(
                "good-task", RelayTaskOutcomeStatus.Committed, "h", "sha", null);
            var now = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);

            new ObsidianSummaryWriter().Write(
                layout, repoRoot, "good-task", outcome, "# Good Task\n\n.", null, now);

            var expected = layout.SummaryPath("good-task", new DateOnly(2026, 6, 21));
            Assert.True(File.Exists(expected));
        }
        finally { Cleanup(repoRoot, layout, vaultRoot); }
    }
}
