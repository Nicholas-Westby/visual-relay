using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // Hydrated from config on load; O(1) lookup for the selected-task toggle.
    private HashSet<string> _skipTestsTaskIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the currently selected task skips stage 5 (Author-tests).
    /// Getter returns true when the selected task's id is in the skip set.
    /// Setter adds or removes the id from the skip set and persists to config.
    /// </summary>
    public bool SelectedTaskSkipsTests
    {
        get => SelectedTask is not null && _skipTestsTaskIds.Contains(SelectedTask.Id);
        set
        {
            if (SelectedTask is null || string.IsNullOrEmpty(RootPath))
                return;

            var changed = value
                ? _skipTestsTaskIds.Add(SelectedTask.Id)
                : _skipTestsTaskIds.Remove(SelectedTask.Id);

            if (changed)
            {
                RelayConfigWriter.SetSkipTests(RootPath, SelectedTask.Id, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(SkipTestsLabel));
            }
        }
    }

    /// <summary>
    /// Human-readable label for the skip-tests toggle.
    /// </summary>
    public string SkipTestsLabel =>
        SelectedTask is not null && !string.IsNullOrEmpty(RootPath)
            ? "Skip automated testing"
            : string.Empty;

    /// <summary>
    /// Whether the skip-tests toggle can be interacted with. False when no
    /// task is selected or the repo isn't initialized.
    /// </summary>
    public bool CanToggleSkipTests =>
        SelectedTask is not null && !string.IsNullOrEmpty(RootPath) && !IsBusy;

    /// <summary>
    /// Hydrates the skip set from config. Called from
    /// <see cref="ReloadTaskListAsync"/> after config is loaded.
    /// </summary>
    private void HydrateSkipTests(RelayConfig config)
    {
        _skipTestsTaskIds = new HashSet<string>(config.SkipTestsTaskIds ?? [], StringComparer.Ordinal);
        OnPropertyChanged(nameof(SelectedTaskSkipsTests));
        OnPropertyChanged(nameof(SkipTestsLabel));
    }
}
