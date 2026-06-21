using VisualRelay.Core.ObsidianBridge;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class ObsidianSummaryWriterTests
{
    private static (string RepoRoot, ObsidianVaultLayout Layout) Setup(
        string repoName = "test-repo")
    {
        var vaultRoot = Path.Combine(Path.GetTempPath(), "vr-obsidian-summary-tests", Guid.NewGuid().ToString("N"));
        var repoRoot = Path.Combine(Path.GetTempPath(), "vr-obsidian-summary-repo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        Directory.CreateDirectory(Path.Combine(repoRoot, "llm-tasks"));
        var layout = new ObsidianVaultLayout(vaultRoot, repoName);
        layout.EnsureScaffold();
        return (repoRoot, layout);
    }

    private static void Cleanup(string repoRoot, ObsidianVaultLayout layout)
    {
        TestFileSystem.DeleteDirectoryResilient(repoRoot);
        TestFileSystem.DeleteDirectoryResilient(
            Path.GetDirectoryName(layout.RepoDir)!); // vault root
    }

    private static void WriteReport(string root, string taskId, int stage, int attempt,
        string timestamp, string model = "cheap", double durationS = 2.0)
    {
        var taskDir = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(
            Path.Combine(taskDir, $"stage{stage}-attempt{attempt}.report.json"),
            $$"""
            {
              "timestamp": "{{timestamp}}",
              "model": "{{model}}",
              "result": { "outcome": "success" },
              "stats": { "total_llm_time_s": {{durationS}} },
              "timeline": [{ "type": "llm_call", "prompt_tokens_est": 500 }]
            }
            """);
    }

    private static void WriteStatusRecord(string root, string taskId,
        params StageStatusEntry[] entries)
    {
        var taskDir = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        StageStatusRecord.WriteAsync(taskDir, entries).GetAwaiter().GetResult();
    }

    // ── Build: committed ──────────────────────────────────────────────

    [Fact]
    public void Build_Committed_RendersCommitShaCostDurationAndStages()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "feature-a", 1, 1,
                "2026-06-20T14:00:00+00:00", "cheap-kimi", 2.5);
            WriteReport(repoRoot, "feature-a", 2, 1,
                "2026-06-20T14:30:00+00:00", "cheap-kimi", 3.0);
            WriteStatusRecord(repoRoot, "feature-a",
                new StageStatusEntry(1, "Ideate", "Done"),
                new StageStatusEntry(2, "Research", "Done"));

            // Also write the task spec so the writer can embed it.
            var taskPath = Path.Combine(repoRoot, "llm-tasks", "feature-a", "feature-a.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# Feature A\n\nBuild feature A.");

            var outcome = new RelayTaskOutcome(
                "feature-a", RelayTaskOutcomeStatus.Committed, "hash123", "abc1234", null);
            var now = new DateTimeOffset(2026, 6, 20, 14, 30, 0, TimeSpan.Zero);

            var writer = new ObsidianSummaryWriter();
            var markdown = writer.Build(repoRoot, "feature-a", outcome, "# Feature A\n\nBuild feature A.",
                null, now);

            // Frontmatter.
            Assert.Contains("vr-task-id: feature-a", markdown, StringComparison.Ordinal);
            Assert.Contains("vr-status: committed", markdown, StringComparison.Ordinal);
            Assert.Contains("vr-commit: abc1234", markdown, StringComparison.Ordinal);

            // Status line.
            Assert.Contains("**Status:** Committed", markdown, StringComparison.Ordinal);
            Assert.Contains("`abc1234`", markdown, StringComparison.Ordinal);

            // Stages table.
            Assert.Contains("## Stages", markdown, StringComparison.Ordinal);
            Assert.Contains("| 1 | Ideate | Done |", markdown, StringComparison.Ordinal);
            Assert.Contains("| 2 | Research | Done |", markdown, StringComparison.Ordinal);

            // Task spec embedded.
            Assert.Contains("## Task", markdown, StringComparison.Ordinal);
            Assert.Contains("Build feature A.", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(repoRoot, layout);
        }
    }

    [Fact]
    public void Build_Committed_EmbedsSpecMarkdown()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "embed-test", 1, 1,
                "2026-06-20T10:00:00+00:00");
            WriteStatusRecord(repoRoot, "embed-test",
                new StageStatusEntry(1, "Ideate", "Done"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "embed-test", "embed-test.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# Embed Test\n\nSome multiline\nspec content.");

            var outcome = new RelayTaskOutcome(
                "embed-test", RelayTaskOutcomeStatus.Committed, "h", "sha1", null);
            var now = new DateTimeOffset(2026, 6, 20, 10, 0, 0, TimeSpan.Zero);

            var writer = new ObsidianSummaryWriter();
            var markdown = writer.Build(repoRoot, "embed-test", outcome,
                "# Embed Test\n\nSome multiline\nspec content.", null, now);

            Assert.Contains("Some multiline\nspec content.", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(repoRoot, layout);
        }
    }

    // ── Build: flagged ────────────────────────────────────────────────

    [Fact]
    public void Build_Flagged_RendersNeedsReviewAndReason()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "flagged-task", 1, 1,
                "2026-06-20T16:00:00+00:00");
            WriteStatusRecord(repoRoot, "flagged-task",
                new StageStatusEntry(1, "Ideate", "Done"),
                new StageStatusEntry(2, "Research", "Flagged", Error: "commit rejected"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "flagged-task", "flagged-task.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# Flagged\n\nThis one hit a wall.");

            var outcome = new RelayTaskOutcome(
                "flagged-task", RelayTaskOutcomeStatus.Flagged, null, null, "commit rejected: empty commit");
            var now = new DateTimeOffset(2026, 6, 20, 16, 0, 0, TimeSpan.Zero);

            var writer = new ObsidianSummaryWriter();
            var markdown = writer.Build(repoRoot, "flagged-task", outcome,
                "# Flagged\n\nThis one hit a wall.", null, now);

            Assert.Contains("vr-status: needs-review", markdown, StringComparison.Ordinal);
            Assert.Contains("**Status:** Needs review", markdown, StringComparison.Ordinal);
            Assert.Contains("commit rejected", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(repoRoot, layout);
        }
    }

    // ── Build: failed ─────────────────────────────────────────────────

    [Fact]
    public void Build_Failed_RendersFailedStatus()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "failed-task", 1, 1,
                "2026-06-20T18:00:00+00:00");
            WriteStatusRecord(repoRoot, "failed-task",
                new StageStatusEntry(1, "Ideate", "Failed", Error: "catastrophic error"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "failed-task", "failed-task.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# Failed\n\nBoom.");

            var outcome = new RelayTaskOutcome(
                "failed-task", RelayTaskOutcomeStatus.Failed, null, null, "catastrophic error");
            var now = new DateTimeOffset(2026, 6, 20, 18, 0, 0, TimeSpan.Zero);

            var writer = new ObsidianSummaryWriter();
            var markdown = writer.Build(repoRoot, "failed-task", outcome,
                "# Failed\n\nBoom.", null, now);

            Assert.Contains("vr-status: failed", markdown, StringComparison.Ordinal);
            Assert.Contains("**Status:** Failed", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(repoRoot, layout);
        }
    }

    // ── Build: null outcome inference ─────────────────────────────────

    [Fact]
    public void Build_NullOutcome_InfersStatusFromStatusRecord()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "infer-me", 1, 1,
                "2026-06-20T12:00:00+00:00");
            // All stages done → committed.
            WriteStatusRecord(repoRoot, "infer-me",
                new StageStatusEntry(1, "Ideate", "Done"),
                new StageStatusEntry(2, "Research", "Done"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "infer-me", "infer-me.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# Infer\n\nNo outcome.");

            var now = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero);

            var writer = new ObsidianSummaryWriter();
            var markdown = writer.Build(repoRoot, "infer-me", null,
                "# Infer\n\nNo outcome.", null, now);

            // When outcome is null but all stages are Done, infer committed.
            Assert.Contains("vr-status: committed", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(repoRoot, layout);
        }
    }

    [Fact]
    public void Build_NullOutcomeWithFlaggedStage_InfersNeedsReview()
    {
        var (repoRoot, layout) = Setup();
        try
        {
            WriteReport(repoRoot, "flagged-infer", 1, 1,
                "2026-06-20T13:00:00+00:00");
            WriteStatusRecord(repoRoot, "flagged-infer",
                new StageStatusEntry(1, "Ideate", "Done"),
                new StageStatusEntry(2, "Research", "Flagged", Error: "bad"));

            var taskPath = Path.Combine(repoRoot, "llm-tasks", "flagged-infer", "flagged-infer.md");
            Directory.CreateDirectory(Path.GetDirectoryName(taskPath)!);
            File.WriteAllText(taskPath, "# Flagged Infer\n\n.");

            var now = new DateTimeOffset(2026, 6, 20, 13, 0, 0, TimeSpan.Zero);

            var writer = new ObsidianSummaryWriter();
            var markdown = writer.Build(repoRoot, "flagged-infer", null,
                "# Flagged Infer\n\n.", null, now);

            Assert.Contains("vr-status: needs-review", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(repoRoot, layout);
        }
    }
}
