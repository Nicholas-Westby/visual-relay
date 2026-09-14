namespace VisualRelay.App.ViewModels;

// What keeps the run commands disabled. Their enabled state is derived from these lists, so
// the reason the control API reports for a refusal is exactly what the button checks.
public partial class MainWindowViewModel
{
    private bool CanRunSelected() => RunSelectedBlockers().Count == 0;
    private bool CanDrain() => DrainBlockers().Count == 0;

    /// <summary>What keeps Run and Resume disabled right now, in order; empty when they can run.</summary>
    internal IReadOnlyList<RunBlocker> RunSelectedBlockers()
    {
        var blockers = RunBlockersCommonToAll();
        if (SelectedTask is null) blockers.Add(RunBlocker.NothingSelected);
        else if (SelectedTask.IsArchived) blockers.Add(RunBlocker.SelectedArchived);
        else if (_rewritingTaskIds.Contains(SelectedTask.Id)) blockers.Add(RunBlocker.SelectedRewriting);
        return blockers;
    }

    /// <summary>What keeps Run all disabled right now, in order; empty when it can run.</summary>
    internal IReadOnlyList<RunBlocker> DrainBlockers()
    {
        var blockers = RunBlockersCommonToAll();
        if (ShowArchive) blockers.Add(RunBlocker.ArchiveShowing);
        if (Tasks.Count == 0) blockers.Add(RunBlocker.QueueEmpty);
        return blockers;
    }

    private List<RunBlocker> RunBlockersCommonToAll()
    {
        var blockers = new List<RunBlocker>();
        if (IsBusy) blockers.Add(RunBlocker.Busy);
        if (PauseRequested) blockers.Add(RunBlocker.Paused);
        return blockers;
    }
}
