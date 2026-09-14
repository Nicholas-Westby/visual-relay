using VisualRelay.Core.Tasks;

namespace VisualRelay.Tests;

/// <summary>
/// A task is written where the config's tasksDir says, which is where the queue lists it from.
/// Found following apache/commons-lang on the Windows arm: the queue read tasksDir but the writer
/// always used llm-tasks, so a project that keeps its tasks elsewhere got every new task written
/// where the queue never looks.
/// </summary>
public sealed partial class RelayTaskWriterTests
{
    private const string HiddenTasksConfig = """{ "testCmd": "true", "logSources": [], "tasksDir": ".llm-tasks" }""";

    [Fact]
    public async Task CreateAsync_WritesIntoTheConfiguredTasksDirectory_WhereTheQueueListsIt()
    {
        using var repo = TestRepository.Create();
        WriteRawConfig(repo, HiddenTasksConfig);

        var path = await RelayTaskWriter.CreateAsync(repo.Root, "moved-task", "# Moved\n");

        Assert.Equal(Path.Combine(repo.Root, ".llm-tasks", "moved-task", "moved-task.md"), path);
        Assert.False(Directory.Exists(Path.Combine(repo.Root, "llm-tasks")));
        Assert.Contains(await new RelayTaskRepository(repo.Root).ListAsync(), task => task.Id == "moved-task");
    }

    [Fact]
    public async Task ValidateSlug_FindsATaskThatAlreadyExistsInTheConfiguredTasksDirectory()
    {
        using var repo = TestRepository.Create();
        WriteRawConfig(repo, HiddenTasksConfig);
        await RelayTaskWriter.CreateAsync(repo.Root, "taken", "# Taken\n");

        Assert.Contains("already exists", RelayTaskWriter.ValidateSlug("taken", repo.Root), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("""{ "testCmd": "true", "logSources": [] }""")]
    public async Task CreateAsync_WithoutAUsableTasksDirSetting_KeepsTheDefaultDirectory(string? config)
    {
        using var repo = TestRepository.Create();
        if (config is not null)
            WriteRawConfig(repo, config);

        var path = await RelayTaskWriter.CreateAsync(repo.Root, "plain-task", "# Plain\n");

        Assert.Equal(Path.Combine(repo.Root, "llm-tasks", "plain-task", "plain-task.md"), path);
    }

    private static void WriteRawConfig(TestRepository repo, string json)
    {
        Directory.CreateDirectory(Path.Combine(repo.Root, ".relay"));
        File.WriteAllText(Path.Combine(repo.Root, ".relay", "config.json"), json);
    }
}
