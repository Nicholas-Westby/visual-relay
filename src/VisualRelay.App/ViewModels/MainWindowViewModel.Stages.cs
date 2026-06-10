using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// Resets the stage board from <paramref name="taskId"/>'s status.json.
    /// When null, clears the board. Never wipes other tasks' events.
    /// </summary>
    private void ResetStages(string? taskId = null)
    {
        foreach (var stage in Stages)
        {
            stage.Status = "Waiting";
            stage.IsSelected = false;
            stage.ClearMetric();
        }

        if (taskId is null)
            return;

        var taskDir = Path.Combine(RootPath, ".relay", taskId);
        var status = StageStatusRecord.Read(taskDir);
        foreach (var entry in status)
        {
            var stage = Stages.FirstOrDefault(s => s.Number == entry.Stage);
            if (stage is null)
                continue;
            stage.Status = entry.Status;
            if (entry.DurationSeconds is { } dur)
                stage.DurationLabel = FormatDurationLabel(dur);
            if (entry.CostUsd is { } cost)
                stage.CostLabel = MoneyFormatter.Dollars(cost);
            if (entry.Model is { } model)
                stage.ModelLabel = model;
            if (entry.Turns is { } turns)
                stage.TurnsLabel = $"{turns}t";
        }
    }

    private static string FormatDurationLabel(double seconds)
    {
        if (seconds < 60)
            return $"{Math.Max(0, seconds):0}s";
        var minutes = Math.Floor(seconds / 60);
        var remainder = seconds % 60;
        return $"{minutes:0}m {remainder:00}s";
    }
}
