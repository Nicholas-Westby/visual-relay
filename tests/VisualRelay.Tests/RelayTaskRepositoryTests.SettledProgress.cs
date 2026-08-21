using VisualRelay.App.ViewModels;
using VisualRelay.Core.Tasks;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class RelayTaskRepositoryTests
{
    [Fact]
    public async Task ListAsync_AttachesSettledProgressFromStatusRecordNotReports()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        // Two report files on disk deliberately disagree with the status record.
        WriteReport(repo.Root, "alpha", 1, "cheap", 1.0, 1_000);
        WriteReport(repo.Root, "alpha", 2, "cheap", 1.0, 1_000);
        var taskDir = Path.Combine(repo.Root, ".relay", "alpha");
        var entries = Enumerable.Range(1, 10)
            .Select(i => new StageStatusEntry(i, $"Stage {i}", "Done"))
            .Append(new StageStatusEntry(11, "Fix-verify", "Skipped"))
            .Append(new StageStatusEntry(12, "Commit", "Done"))
            .ToList();
        await StageStatusRecord.WriteAsync(taskDir, entries);

        var task = Assert.Single(await new RelayTaskRepository(repo.Root).ListAsync());

        // The metrics count keeps coming from report files.
        Assert.Equal(2, task.CompletedStageCount);
        // The progress counts come from the status record.
        Assert.Equal(12, task.SettledStageCount);
        Assert.Equal(12, task.PipelineStageCount);

        // After live progress, MarkIdle falls back to the settled idle branch,
        // never to zero.
        var row = new TaskRowViewModel(task);
        row.MarkRunning();
        row.RecordStageCompleted(6);
        row.MarkIdle();
        Assert.Equal(1.0, row.ProgressFraction, precision: 6);
    }
}
