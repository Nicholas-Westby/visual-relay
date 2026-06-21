using VisualRelay.Core.ObsidianBridge;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Write, source-GUID, and vr-repo tests for <see cref="ObsidianSummaryWriter"/>.
/// Split from <see cref="ObsidianSummaryWriterTests"/> to stay under the 300-line guard.
/// </summary>
public sealed class ObsidianSummaryWriterWriteTests : IDisposable
{
    private static (string RepoRoot, ObsidianVaultLayout Layout) Setup(
        string repoName = "test-repo")
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "vr-obsidian-summary-tests",
            Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(Path.GetTempPath(), "vr-obsidian-summary-repo",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(Path.Combine(repoRoot, "llm-tasks"));

        var layout = new ObsidianVaultLayout(vaultRoot, repoName);
        layout.EnsureScaffold();
        return (repoRoot, layout);
    }

    private static void Cleanup(string repoRoot, ObsidianVaultLayout layout)
    {
        TestFileSystem.DeleteDirectoryResilient(repoRoot);
        TestFileSystem.DeleteDirectoryResilient(layout.RepoDir);
    }

    /// <summary>
    /// Writes a minimal stage report JSON so ReadTaskMetric produces a real StageRunMetric.
    /// </summary>
    private static void WriteReport(string repoRoot, string taskId, int stage, int attempt,
        string timestampIso, string model = "cheap-kimi", double durationS = 26.0, int turns = 7)
    {
        var dir = Path.Combine(repoRoot, ".relay", taskId);
        Directory.CreateDirectory(dir);
        var reportPath = Path.Combine(dir, $"stage{stage}-attempt{attempt}.report.json");

        var json = $$"""
            {
              "timestamp": "{{timestampIso}}",
              "model": "{{model}}",
              "result": { "outcome": "success" },
              "stats": { "total_llm_time_s": {{durationS}} },
              "timeline": [{ "type": "llm_call", "prompt_tokens_est": {{turns * 100}} }]
            }
            """;
        File.WriteAllText(reportPath, json);
    }

    private static void WriteStatusRecord(string repoRoot, string taskId,
        params StageStatusEntry[] entries)
    {
        var dir = Path.Combine(repoRoot, ".relay", taskId);
        Directory.CreateDirectory(dir);
        var statusPath = Path.Combine(dir, "status.json");

        var entryJson = string.Join(",\n  ",
            entries.Select(e => $$"""
                {"stage": {{e.Stage}}, "name": "{{e.Name}}", "status": "{{e.Status}}"{{(e.Error is not null ? $", \"error\": \"{e.Error}\"" : "")}}}
                """));
        File.WriteAllText(statusPath, $"""
            [
              {entryJson}
            ]
            """);
    }

    public void Dispose()
    {
        // Individual tests clean up themselves.
    }

    [Fact]
    public void Write_UsesMaxStageTimestampAsCompletionDate()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "date-test", 1, 1,
                "2026-06-20T14:00:00+00:00", durationS: 1.0);
            WriteReport(repoRoot, "date-test", 2, 1,
                "2026-06-20T15:30:00+00:00", durationS: 2.0);
            WriteStatusRecord(repoRoot, "date-test",
                new StageStatusEntry(1, "Ideate", "Done"),
                new StageStatusEntry(2, "Research", "Done"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "date-test", "date-test.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# Date Test\n\n.");

            var outcome = new RelayTaskOutcome(
                "date-test", RelayTaskOutcomeStatus.Committed, "h", "sha", null);
            var now = new DateTimeOffset(2026, 6, 20, 16, 0, 0, TimeSpan.Zero);

            var writer = new ObsidianSummaryWriter();
            writer.Write(layout, repoRoot, "date-test", outcome,
                "# Date Test\n\n.", null, now);

            var expectedPath = layout.SummaryPath("date-test", new DateOnly(2026, 6, 20));
            Assert.True(File.Exists(expectedPath));

            var content = File.ReadAllText(expectedPath);
            Assert.Contains("2026-06-20T15:30:00", content, StringComparison.Ordinal);
        }
        finally { Cleanup(repoRoot, layout); }
    }

    [Fact]
    public void Write_OverwritesExistingSummary()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "overwrite", 1, 1, "2026-06-21T10:00:00+00:00");
            WriteStatusRecord(repoRoot, "overwrite",
                new StageStatusEntry(1, "Ideate", "Done"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "overwrite", "overwrite.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# Overwrite\n\nFirst version.");

            var outcome1 = new RelayTaskOutcome(
                "overwrite", RelayTaskOutcomeStatus.Committed, "h1", "sha1", null);
            var now = new DateTimeOffset(2026, 6, 21, 10, 0, 0, TimeSpan.Zero);
            var writer = new ObsidianSummaryWriter();

            writer.Write(layout, repoRoot, "overwrite", outcome1,
                "# Overwrite\n\nFirst version.", null, now);

            var summaryPath = layout.SummaryPath("overwrite", new DateOnly(2026, 6, 21));
            Assert.True(File.Exists(summaryPath));
            var firstContent = File.ReadAllText(summaryPath);
            Assert.Contains("First version", firstContent, StringComparison.Ordinal);

            writer.Write(layout, repoRoot, "overwrite", outcome1,
                "# Overwrite\n\nSecond version.", null, now);

            var secondContent = File.ReadAllText(summaryPath);
            Assert.Contains("Second version", secondContent, StringComparison.Ordinal);
            Assert.DoesNotContain("First version", secondContent, StringComparison.Ordinal);
        }
        finally { Cleanup(repoRoot, layout); }
    }

    [Fact]
    public void Write_CreatesCompletedDateDirectoryIfMissing()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "new-dir", 1, 1, "2026-06-21T09:00:00+00:00");
            WriteStatusRecord(repoRoot, "new-dir",
                new StageStatusEntry(1, "Ideate", "Done"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "new-dir", "new-dir.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# New Dir\n\n.");

            var outcome = new RelayTaskOutcome(
                "new-dir", RelayTaskOutcomeStatus.Committed, "h", "sha", null);
            var now = new DateTimeOffset(2026, 6, 21, 9, 0, 0, TimeSpan.Zero);

            var dateDir = layout.CompletedDir(new DateOnly(2026, 6, 21));
            if (Directory.Exists(dateDir))
                Directory.Delete(dateDir, true);

            var writer = new ObsidianSummaryWriter();
            writer.Write(layout, repoRoot, "new-dir", outcome,
                "# New Dir\n\n.", null, now);

            Assert.True(Directory.Exists(dateDir));
            Assert.True(File.Exists(Path.Combine(dateDir, "new-dir.md")));
        }
        finally { Cleanup(repoRoot, layout); }
    }

    [Fact]
    public void Build_IncludesSourceGuidWhenProvided()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "from-obsidian", 1, 1, "2026-06-20T11:00:00+00:00");
            WriteStatusRecord(repoRoot, "from-obsidian",
                new StageStatusEntry(1, "Ideate", "Done"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "from-obsidian", "from-obsidian.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# From Obsidian\n\nImported.");

            var sourceGuid = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            var outcome = new RelayTaskOutcome(
                "from-obsidian", RelayTaskOutcomeStatus.Committed, "h", "sha", null);
            var now = new DateTimeOffset(2026, 6, 20, 11, 0, 0, TimeSpan.Zero);

            var writer = new ObsidianSummaryWriter();
            var markdown = writer.Build(repoRoot, "from-obsidian", outcome,
                "# From Obsidian\n\nImported.", sourceGuid, now);

            Assert.Contains("vr-source-guid: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                markdown, StringComparison.Ordinal);
        }
        finally { Cleanup(repoRoot, layout); }
    }

    [Fact]
    public void Build_OmitsSourceGuidWhenNull()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "no-source", 1, 1, "2026-06-20T11:00:00+00:00");
            WriteStatusRecord(repoRoot, "no-source",
                new StageStatusEntry(1, "Ideate", "Done"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "no-source", "no-source.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# No Source\n\nDirect.");

            var outcome = new RelayTaskOutcome(
                "no-source", RelayTaskOutcomeStatus.Committed, "h", "sha", null);
            var now = new DateTimeOffset(2026, 6, 20, 11, 0, 0, TimeSpan.Zero);

            var writer = new ObsidianSummaryWriter();
            var markdown = writer.Build(repoRoot, "no-source", outcome,
                "# No Source\n\nDirect.", null, now);

            Assert.Contains("vr-source-guid:", markdown, StringComparison.Ordinal);
        }
        finally { Cleanup(repoRoot, layout); }
    }

    [Fact]
    public void Build_IncludesVrRepoInFrontmatter()
    {
        var (repoRoot, layout) = Setup("my-awesome-project");
        try
        {
            WriteReport(repoRoot, "repo-test", 1, 1, "2026-06-20T12:00:00+00:00");
            WriteStatusRecord(repoRoot, "repo-test",
                new StageStatusEntry(1, "Ideate", "Done"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "repo-test", "repo-test.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# Repo Test\n\n.");

            var outcome = new RelayTaskOutcome(
                "repo-test", RelayTaskOutcomeStatus.Committed, "h", "sha", null);
            var now = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

            var writer = new ObsidianSummaryWriter();
            var markdown = writer.Build(repoRoot, "repo-test", outcome,
                "# Repo Test\n\n.", null, now);

            var repoName = Path.GetFileName(repoRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            Assert.Contains($"vr-repo: {repoName}", markdown, StringComparison.Ordinal);
        }
        finally { Cleanup(repoRoot, layout); }
    }
}
