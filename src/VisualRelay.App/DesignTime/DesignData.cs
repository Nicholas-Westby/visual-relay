using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.App.DesignTime;

/// <summary>
/// Populated view models for the Avalonia previewer (Design.DataContext).
/// Never constructed at runtime: the XAML compiler strips Design.* property
/// assignments outside the designer.
/// </summary>
public static class DesignData
{
    /// <summary>Richest single card: running, selected, mid-progress.</summary>
    public static TaskRowViewModel Card { get; }

    /// <summary>Queue/main-window context with one row per card state.</summary>
    public static MainWindowViewModel Main { get; }

    static DesignData()
    {
        Card = new TaskRowViewModel(NewItem("03-survive-display-sleep-at-launch", completedStages: 8));
        Card.MarkRunning(9, "Fix");
        Card.RunningElapsedLabel = "14m 12s";
        for (var stage = 1; stage <= 8; stage++)
        {
            Card.RecordStageCompleted(stage);
        }
        Card.IsSelected = true;

        Main = new MainWindowViewModel();
        Main.Tasks.Add(Card);
        Main.Tasks.Add(new TaskRowViewModel(NewItem("04-cost-panel-concrete-models")));
        Main.Tasks.Add(new TaskRowViewModel(NewItem("02-fix-task-author-real-evidence",
            reviewReason: "Stage 08 visual-review skipped: renderer unavailable",
            costUsd: 0.34, durationSeconds: 512, completedStages: 12)));
        Main.Tasks.Add(new TaskRowViewModel(NewItem("01-replace-target-env-base-not-overlay",
            costUsd: 0.06, durationSeconds: 138, completedStages: 5)));
        Main.Tasks.Add(new TaskRowViewModel(NewItem("00-bootstrap-project",
            archived: true, costUsd: 0.42, durationSeconds: 947, completedStages: 12))
        { DayHeader = "Today: $0.42, $0.21/task, $13/mo" });
        // Deliberately NOT setting Main.SelectedTask: OnSelectedTaskChanged
        // (MainWindowViewModel.Commands.cs) kicks SelectTaskAsync, which reads
        // the fabricated markdown path from disk and surfaces the failure into
        // StatusText — polluting every preview. Card.IsSelected above already
        // drives all selected-state card visuals.
        Main.StatusText = "Running 03-survive-display-sleep-at-launch — Stage 09 · Fix";
    }

    private static RelayTaskItem NewItem(
        string id, string? reviewReason = null, bool archived = false,
        double costUsd = 0, double durationSeconds = 0, int completedStages = 0)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, "Dev", "acme", "llm-tasks", id);
        return new RelayTaskItem(id, Path.Combine(dir, $"{id}.md"), dir, false, [],
            reviewReason, archived, null, costUsd, durationSeconds, completedStages);
    }
}
