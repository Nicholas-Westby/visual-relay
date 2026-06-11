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
        WriteReport(repo.Root, "alpha", 1, "cheap", 2.5, 1_000);

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

    [Fact]
    public async Task ListAsync_NoConfig_StillListsTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n"); // note: no WriteConfig
        var tasks = await new RelayTaskRepository(repo.Root).ListAsync();
        Assert.Equal(["alpha"], tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task ListAsync_IncompleteConfig_StillListsTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), """{ "logSources": [] }""");
        var tasks = await new RelayTaskRepository(repo.Root).ListAsync();
        Assert.Equal(["alpha"], tasks.Select(t => t.Id));
    }

    [Fact]
    public async Task ListAsync_MalformedConfig_ReturnsNoTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteTask("alpha", "# Alpha\n");
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        await File.WriteAllTextAsync(Path.Combine(repo.Root, ".relay", "config.json"), "{ not json");
        var tasks = await new RelayTaskRepository(repo.Root).ListAsync();
        Assert.Empty(tasks);
    }

    [Fact]
    public async Task ListPendingAsync_TreatsExtraMarkdownInTaskFolderAsSiblingNotSeparateTask()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Create a nested task folder with the canonical folder-named markdown
        // plus an extra .md file that must NOT appear as a separate queue entry.
        repo.WriteNestedTask("nested-with-notes", "# Main task\n",
            ("notes.md", "# Notes\nThese are supplementary notes."),
            ("data.json", "{\"field\":42}"));

        var tasks = await new RelayTaskRepository(repo.Root).ListPendingAsync();

        // Only one task: the folder-named markdown.
        Assert.Single(tasks);
        var task = tasks[0];
        Assert.Equal("nested-with-notes", task.Id);
        Assert.True(task.IsNested);
        // Both notes.md and data.json must be siblings.
        Assert.Equal(2, task.SiblingPaths.Count);
        Assert.Contains(task.SiblingPaths, s => s.EndsWith("notes.md", StringComparison.Ordinal));
        Assert.Contains(task.SiblingPaths, s => s.EndsWith("data.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListPendingAsync_OnlyMdFilesDirectlyInTasksRootAreFlatTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        // A nested task folder with extra .md sibling.
        repo.WriteNestedTask("beta", "# Beta\n", ("notes.md", "# Notes"));

        var tasks = await new RelayTaskRepository(repo.Root).ListPendingAsync();

        Assert.Equal(["alpha", "beta"], tasks.Select(t => t.Id).Order());
        var beta = tasks.Single(t => t.Id == "beta");
        Assert.True(beta.IsNested);
        Assert.Single(beta.SiblingPaths);
        Assert.EndsWith("notes.md", beta.SiblingPaths[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListPendingAsync_FolderNamedMdIsOnlyQueueEntryForItsFolder()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        // Create a task folder with two markdown files and one text file.
        var dir = Path.Combine(repo.Root, "llm-tasks", "multi-md");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "multi-md.md"), "# Main");
        await File.WriteAllTextAsync(Path.Combine(dir, "supplement.md"), "# Supplement");
        await File.WriteAllTextAsync(Path.Combine(dir, "readme.txt"), "readme");

        var tasks = await new RelayTaskRepository(repo.Root).ListPendingAsync();

        Assert.Single(tasks);
        var task = tasks[0];
        Assert.Equal("multi-md", task.Id);
        Assert.Equal(2, task.SiblingPaths.Count);
        Assert.Contains(task.SiblingPaths, s => s.EndsWith("supplement.md", StringComparison.Ordinal));
        Assert.Contains(task.SiblingPaths, s => s.EndsWith("readme.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadTaskContextAsync_InlinesMdSiblingsAfterDiscoveryFix()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteNestedTask("with-md-context", "# Main\n",
            ("supplement.md", "## Supplement\n\nExtra markdown context."),
            ("form.html", "<div>UI</div>"));

        var task = Assert.Single(await new RelayTaskRepository(repo.Root).ListPendingAsync());
        var input = await new RelayTaskRepository(repo.Root).ReadTaskInputAsync(task);

        // The .md sibling must be inlined into Context (TextExtensions includes "md").
        Assert.Contains("### supplement.md", input.Context, StringComparison.Ordinal);
        Assert.Contains("Extra markdown context.", input.Context, StringComparison.Ordinal);
        // The existing non-.md sibling still works.
        Assert.Contains("### form.html", input.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListCompletedAsync_TreatsExtraMarkdownInArchivedFolderAsSibling()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var completed = Path.Combine(repo.Root, "llm-tasks", "completed");
        var batchDir = Path.Combine(completed, "batch-1");
        Directory.CreateDirectory(batchDir);
        await File.WriteAllTextAsync(Path.Combine(batchDir, "DONE-archived.md"), "# Archived\n");
        await File.WriteAllTextAsync(Path.Combine(batchDir, "DONE-archived-notes.md"), "# Notes\n");
        await File.WriteAllTextAsync(Path.Combine(batchDir, "schema.json"), "{}");

        var tasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();

        // Only one archived task: "archived". The other .md is a sibling.
        Assert.Single(tasks);
        var task = tasks[0];
        Assert.Equal("archived", task.Id);
        Assert.True(task.IsArchived);
        Assert.Equal(2, task.SiblingPaths.Count);
        Assert.Contains(task.SiblingPaths, s => s.EndsWith("DONE-archived-notes.md", StringComparison.Ordinal));
        Assert.Contains(task.SiblingPaths, s => s.EndsWith("schema.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListCompletedAsync_ListsTopLevelDoneFileAsArchived()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("DONE-alpha", "# Alpha\n");

        var tasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();

        var task = Assert.Single(tasks);
        Assert.Equal("alpha", task.Id);
        Assert.True(task.IsArchived);
        Assert.False(task.IsNested);
        Assert.Null(task.ArchiveBatch);
        Assert.Equal("Completed", task.StateLabel);
        Assert.Empty(task.SiblingPaths);
    }

    [Fact]
    public async Task ListCompletedAsync_ReturnsBothTopLevelAndNestedCompletedTasks()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("DONE-top", "# Top\n");
        var nestedDir = Path.Combine(repo.Root, "llm-tasks", "completed", "batch-2");
        Directory.CreateDirectory(nestedDir);
        await File.WriteAllTextAsync(Path.Combine(nestedDir, "DONE-nested.md"), "# Nested\n");

        var tasks = await new RelayTaskRepository(repo.Root).ListCompletedAsync();

        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t => Assert.True(t.IsArchived));
        var top = Assert.Single(tasks, t => t.Id == "top");
        Assert.False(top.IsNested);
        Assert.Null(top.ArchiveBatch);
        var nested = Assert.Single(tasks, t => t.Id == "nested");
        Assert.True(nested.IsArchived);
        // Newest-first: nested was written after top.
        Assert.Equal("nested", tasks[0].Id);
        Assert.Equal("top", tasks[1].Id);
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
