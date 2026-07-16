using VisualRelay.App.ViewModels;
using VisualRelay.Domain;
using static VisualRelay.Tests.RelayEventTestDispatch;

namespace VisualRelay.Tests;

/// <summary>
/// When either stage of the review pair (7–Review / 8–Visual-review) flags, the
/// sibling stage card must stop its live elapsed timer — otherwise the 1-second
/// <c>UpdateRunningElapsedLabels</c> ticker keeps computing a growing elapsed
/// label for the orphaned stage forever (or until a task switch).
/// </summary>
[Collection("Headless")]
public sealed class ReviewPairOrphanedStageTimerTests
{
    private const int Stage7Index = 6;
    private const int Stage8Index = 7;

    // ── Helpers ────────────────────────────────────────────────────────────

    private static async Task<(MainWindowViewModel vm, TestRepository repo)> NewViewModelAsync(TestRepository repo, string taskId)
    {
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await vm.LoadInitialAsync();
        vm.SelectedTask = vm.Tasks.First(t => t.Id == taskId);
        await vm.LastSelectionLoad!;
        return (vm, repo);
    }

    /// <summary>
    /// Sets both review-pair stages (7 and 8) to Running on the shared
    /// <c>Stages</c> board and wires up the task-level running-stage tracking
    /// so the board mirrors what happens during an actual drain.
    /// </summary>
    private static void StartReviewPair(MainWindowViewModel vm, string taskId)
    {
        vm.RestoreRunningTaskState(taskId, 7, "Review");
        // RestoreRunningTaskState wires the task's running-stage tracking but
        // never touches the stage cards — set both review-pair stages Running.
        vm.Stages[Stage7Index].MarkRunning(DateTimeOffset.UtcNow);
        Dispatch(vm, StageStart(taskId, 8, DateTimeOffset.UtcNow));
    }

    // ── Selected-task sibling settling ─────────────────────────────────────

    /// <summary>
    /// When stage 7 (Review) flags while both review-pair stages are Running,
    /// the sibling stage 8 must stop ticking — it has no terminal event coming
    /// and must not stay "Running" forever.
    /// </summary>
    [AvaloniaFact]
    public async Task Stage7Flagged_SiblingStage8StopsTicking()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("review-flags-7", "# Review flags at stage 7\n");
        var (vm, _) = await NewViewModelAsync(repo, "review-flags-7");

        // Both review-pair stages running.
        StartReviewPair(vm, "review-flags-7");
        Assert.Equal("Running", vm.Stages[Stage7Index].Status);
        Assert.Equal("Running", vm.Stages[Stage8Index].Status);

        // Stage 7 flags — e.g. the Review subagent returns red.
        Dispatch(vm, Flagged("review-flags-7", 7, DateTimeOffset.UtcNow));

        // The flagged stage must transition away from Running.
        Assert.Equal("Flagged", vm.Stages[Stage7Index].Status);
        Assert.Equal(string.Empty, vm.Stages[Stage7Index].ElapsedLabel);

        // The sibling must also leave Running — no terminal event is coming for it.
        Assert.NotEqual("Running", vm.Stages[Stage8Index].Status);
        Assert.Equal(string.Empty, vm.Stages[Stage8Index].ElapsedLabel);
    }

    /// <summary>
    /// When stage 8 (Visual-review) flags while both review-pair stages are
    /// Running, the sibling stage 7 must stop ticking — symmetric to the
    /// stage-7-flag case.
    /// </summary>
    [AvaloniaFact]
    public async Task Stage8Flagged_SiblingStage7StopsTicking()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("visual-flags-8", "# Visual-review flags at stage 8\n");
        var (vm, _) = await NewViewModelAsync(repo, "visual-flags-8");

        StartReviewPair(vm, "visual-flags-8");
        Assert.Equal("Running", vm.Stages[Stage7Index].Status);
        Assert.Equal("Running", vm.Stages[Stage8Index].Status);

        // Stage 8 flags — e.g. the visual check returns red.
        Dispatch(vm, Flagged("visual-flags-8", 8, DateTimeOffset.UtcNow));

        Assert.Equal("Flagged", vm.Stages[Stage8Index].Status);
        Assert.Equal(string.Empty, vm.Stages[Stage8Index].ElapsedLabel);

        // Sibling stage 7 must not stay Running.
        Assert.NotEqual("Running", vm.Stages[Stage7Index].Status);
        Assert.Equal(string.Empty, vm.Stages[Stage7Index].ElapsedLabel);
    }

    // ── Background (non-selected) task flagged ──────────────────────────────

    /// <summary>
    /// When a flagged event arrives for a task the user is NOT currently viewing,
    /// the flagged stage card on the shared <c>Stages</c> board must still stop
    /// ticking — the pre-guard <c>ApplyStageEventToBoard</c> call stops the timer
    /// regardless of which task is selected.
    /// </summary>
    [AvaloniaFact]
    public async Task FlaggedEventForNonSelectedTask_StopsStageCardTimer()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("selected-task", "# User is viewing this task\n");
        repo.WriteTask("background-task", "# Drains in the background\n");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        await vm.LoadInitialAsync();

        // Select task A — the shared Stages board now shows task A's statuses.
        vm.SelectedTask = vm.Tasks.First(t => t.Id == "selected-task");
        await vm.LastSelectionLoad!;

        // Manually set stage 7 to Running on the shared board (bypasses the
        // selected-task guard so the stage card shows a ticking timer).
        var stage7 = vm.Stages[Stage7Index];
        stage7.MarkRunning(DateTimeOffset.UtcNow);
        Assert.Equal("Running", stage7.Status);

        // Dispatch a flagged event for the background (non-selected) task.
        Dispatch(vm, Flagged("background-task", 7, DateTimeOffset.UtcNow));

        // The stage card must stop ticking — the flagged event reaches
        // ApplyStageEventToBoard before the selected-task guard.
        Assert.NotEqual("Running", vm.Stages[Stage7Index].Status);
        Assert.Equal(string.Empty, vm.Stages[Stage7Index].ElapsedLabel);
    }

    // ── Status.json round-trip ──────────────────────────────────────────────

    /// <summary>
    /// After a flagged event settles the sibling on the live board, the settled
    /// status must survive a write-then-read round-trip through status.json so
    /// the orphaned "Running" state cannot be resurrected by a task switch /
    /// <c>LoadRunHistoryAsync</c>.
    /// </summary>
    [AvaloniaFact]
    public async Task FlaggedStage_SettledSiblingStatus_SurvivesStatusJsonRoundTrip()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        var taskId = "roundtrip";
        repo.WriteTask(taskId, "# Round-trip test\n");
        var (vm, _) = await NewViewModelAsync(repo, taskId);

        // Both review-pair stages running, then stage 7 flags.
        StartReviewPair(vm, taskId);
        Assert.Equal("Running", vm.Stages[Stage7Index].Status);
        Assert.Equal("Running", vm.Stages[Stage8Index].Status);
        Dispatch(vm, Flagged(taskId, 7, DateTimeOffset.UtcNow));

        // After the flag, the sibling must be settled on the live board.
        var siblingStatus = vm.Stages[Stage8Index].Status;
        Assert.NotEqual("Running", siblingStatus);

        // Write the current board statuses to disk (simulating the driver's
        // FlagAsync status.json write), then read them back.
        var taskDir = Path.Combine(repo.Root, ".relay", taskId);
        Directory.CreateDirectory(taskDir);
        var entries = vm.Stages.Select(s =>
            new StageStatusEntry(s.Number, s.Name, s.Status)).ToList();
        await StageStatusRecord.WriteAsync(taskDir, entries);

        var readBack = StageStatusRecord.Read(taskDir);
        var stage8Entry = readBack.First(e => e.Stage == 8);

        // The orphaned "Running" must not survive the round-trip.
        Assert.NotEqual("Running", stage8Entry.Status);
    }
}
