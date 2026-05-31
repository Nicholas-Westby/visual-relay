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
}
