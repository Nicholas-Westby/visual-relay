using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the best-effort skipTestsTaskIds prune in
/// <see cref="TaskCompletionArchive.RetireAsync"/>.
/// </summary>
public sealed class TaskCompletionArchiveSkipTestsPruneTests
{
    [Fact]
    public async Task RetireAsync_PrunesSkipTestsTaskId()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");
        RelayConfigWriter.SetSkipTests(repo.Root, "ship-status", enabled: true);
        RelayConfigWriter.SetSkipTests(repo.Root, "other-task", enabled: true);
        repo.WriteTask("ship-status", "# Ship status\n");

        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "ship-status.md");
        var task = new RelayTaskItem(
            Id: "ship-status",
            MarkdownPath: markdownPath,
            TaskDirectory: Path.Combine(repo.Root, "llm-tasks"),
            IsNested: false,
            SiblingPaths: []);

        var config = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, config.Status);
        Assert.Contains("ship-status", config.Config.SkipTestsTaskIds!);
        Assert.Contains("other-task", config.Config.SkipTestsTaskIds!);

        var result = TaskCompletionArchive.RetireAsync(repo.Root, config.Config, "ship-status", task);
        Assert.NotNull(result);

        var after = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, after.Status);
        Assert.DoesNotContain("ship-status", after.Config.SkipTestsTaskIds!);
        Assert.Contains("other-task", after.Config.SkipTestsTaskIds!);
        Assert.Equal("dotnet test", after.Config.TestCommand);
    }

    [Fact]
    public async Task RetireAsync_NotInSkipTestsSet_LeavesConfigUnchanged()
    {
        using var repo = TestRepository.Create();
        RelayConfigWriter.Write(repo.Root, "dotnet test");
        RelayConfigWriter.SetSkipTests(repo.Root, "other-task", enabled: true);
        repo.WriteTask("ship-status", "# Ship status\n");

        var configPath = Path.Combine(repo.Root, ".relay", "config.json");
        Assert.True(File.Exists(configPath));
        var lastWriteBefore = File.GetLastWriteTimeUtc(configPath);

        var markdownPath = Path.Combine(repo.Root, "llm-tasks", "ship-status.md");
        var task = new RelayTaskItem(
            Id: "ship-status",
            MarkdownPath: markdownPath,
            TaskDirectory: Path.Combine(repo.Root, "llm-tasks"),
            IsNested: false,
            SiblingPaths: []);

        var config = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Equal(RelayConfigStatus.Loaded, config.Status);

        var result = TaskCompletionArchive.RetireAsync(repo.Root, config.Config, "ship-status", task);
        Assert.NotNull(result);

        // File timestamp must be unchanged — no spurious rewrite.
        var lastWriteAfter = File.GetLastWriteTimeUtc(configPath);
        Assert.Equal(lastWriteBefore, lastWriteAfter);

        // "other-task" must survive in the loaded config.
        var after = await RelayConfigLoader.TryLoadAsync(repo.Root);
        Assert.Contains("other-task", after.Config.SkipTestsTaskIds!);
    }
}
