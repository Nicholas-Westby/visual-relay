using VisualRelay.App.ViewModels;

namespace VisualRelay.App.Services;

public sealed partial class ControlApi
{
    /// <summary>
    /// Why a refused run command is disabled, naming the command that would unblock it
    /// where there is one; null for a command whose gate has no blockers to report. Must
    /// be called on the UI thread (it reads view-model state).
    /// </summary>
    private string? DisabledReason(string name)
    {
        var blockers = name switch
        {
            "run-all" => viewModel.DrainBlockers(),
            "run-selected" or "resume" => viewModel.RunSelectedBlockers(),
            _ => [],
        };
        return blockers.Count == 0 ? null : string.Join("; ", blockers.Select(Describe));
    }

    private static string Describe(RunBlocker blocker) => blocker switch
    {
        RunBlocker.Busy => "a run is in progress: cancel stops it",
        RunBlocker.Paused => "paused: pause-toggle resumes",
        RunBlocker.ArchiveShowing => "the archive is showing: archive-toggle returns to the queue",
        RunBlocker.QueueEmpty => "the queue has no tasks: create-task adds one",
        RunBlocker.NothingSelected => "no task is selected: select-task picks one",
        RunBlocker.SelectedArchived => "the selected task is archived",
        RunBlocker.SelectedRewriting => "the selected task is being rewritten: cancel-rewrite stops it",
        _ => blocker.ToString(),
    };
}
