using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

public sealed class RelayTaskRepositoryTests
{
    [Fact]
    public async Task ListPendingAsync_SkipsDoneIgnoredCompletedAndNeedsReviewTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        repo.WriteTask("DONE-beta", "# Done\n");
        repo.WriteTask("IGNORE-gamma", "# Ignore\n");
        repo.WriteTask("completed/batch-1/DONE-old", "# Old\n");
        repo.WriteTask("_ideation/idea", "# Idea\n");
        repo.WriteNestedTask("nested", "# Nested\n", ("form.html", "<button>Save</button>"));
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay", "nested"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "nested", "NEEDS-REVIEW"), "blocked");

        var tasks = await new RelayTaskRepository(repo.Root).ListPendingAsync();

        Assert.Equal(["alpha"], tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task ListAsync_IncludesNeedsReviewTasksWithReason()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay", "alpha"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "alpha", "NEEDS-REVIEW"), "swival exit 2\nstage 1\n");

        var tasks = await new RelayTaskRepository(repo.Root).ListAsync();

        var task = Assert.Single(tasks);
        Assert.True(task.NeedsReview);
        Assert.Equal("Needs review", task.StateLabel);
        Assert.Equal("swival exit 2", task.ReviewReason);
    }

    [Fact]
    public async Task ReadTaskContextAsync_InlinesSmallTextSiblingFilesForNestedTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask(
            "with-context",
            "batch: 7\n\n# Task\n",
            ("form.html", "<label>Name</label>"),
            ("mock.json", """{"ok":true}"""));

        var task = Assert.Single(await new RelayTaskRepository(repo.Root).ListPendingAsync());
        var input = await new RelayTaskRepository(repo.Root).ReadTaskInputAsync(task);

        Assert.DoesNotContain("batch: 7", input.Markdown, StringComparison.Ordinal);
        Assert.Contains("### form.html", input.Context);
        Assert.Contains("<label>Name</label>", input.Context);
        Assert.Contains("### mock.json", input.Context);
    }

    [Fact]
    public async Task ListAsync_AttachesRunCostAndDuration()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        WriteReport(repo.Root, "alpha", 1, "cheap-kimi", 2.5, 1_000);

        var task = Assert.Single(await new RelayTaskRepository(repo.Root).ListAsync());

        Assert.Equal(1, task.CompletedStageCount);
        Assert.Equal(2.5, task.DurationSeconds, precision: 2);
        Assert.True(task.CostUsd > 0);
        Assert.Contains("$", task.MetricsLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListCompletedAsync_ReturnsFlatAndNestedArchivedTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var completed = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-3");
        Directory.CreateDirectory(completed);
        await File.WriteAllTextAsync(Path.Combine(completed, "DONE-flat.md"), "# Flat\n");
        var nested = Path.Combine(completed, "nested-task");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "DONE-nested-task.md"), "# Nested\n");
        await File.WriteAllTextAsync(Path.Combine(nested, "context.json"), "{}");

        var tasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();

        Assert.Equal(["flat", "nested-task"], tasks.Select(task => task.Id).Order());
        Assert.All(tasks, task => Assert.True(task.IsArchived));
        Assert.Contains(tasks, task => task.Id == "nested-task" && task.IsNested && task.SiblingPaths.Count == 1);
        Assert.All(tasks, task => Assert.Equal("Completed", task.StateLabel));
    }

    private static void WriteReport(string root, string taskId, int stage, string model, double duration, int tokens)
    {
        var taskDirectory = Path.Combine(root, ".relay", taskId);
        Directory.CreateDirectory(taskDirectory);
        File.WriteAllText(
            Path.Combine(taskDirectory, $"stage{stage}-attempt1.report.json"),
            $$"""
            {
              "timestamp": "2026-05-31T20:00:00+00:00",
              "model": "{{model}}",
              "result": { "answer": "ok" },
              "stats": { "total_llm_time_s": {{duration}}, "prompt_cache": { "cached_tokens": 0 } },
              "timeline": [{ "type": "llm_call", "prompt_tokens_est": {{tokens}} }]
            }
            """);
    }
}
