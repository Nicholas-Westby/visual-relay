namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// Per-stage detail (system prompt, parsed input, parsed output) that the
    /// stage-visibility tabs bind to. Updated on stage selection and live
    /// <c>stage_input</c>/<c>stage_done</c> events.
    /// </summary>
    public StageDetailViewModel StageDetail { get; } = new();

    /// <summary>
    /// Refreshes <see cref="StageDetail"/> for the supplied stage (or clears it
    /// when <paramref name="stage"/> is null). Called from
    /// <see cref="SelectStage"/> and <see cref="HandleRelayEvent"/>.
    /// </summary>
    private void RefreshStageDetail(StageRowViewModel? stage)
    {
        var taskDirectory = SelectedTask is { } task
            ? Path.Combine(RootPath, ".relay", task.Id)
            : null;

        StageDetail.Load(stage, taskDirectory);
    }
}
