using VisualRelay.Core.Init;
using VisualRelay.Domain;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // Hydrated from config on load; O(1) lookup for the selected-task toggle.
    private HashSet<string> _boostedTaskIds = new(StringComparer.Ordinal);

    // Hydrated from config on load; default 200. Used to compute the label.
    private int _maxTurns = 200;

    /// <summary>
    /// Whether the currently selected task has the 10× turn-budget boost enabled.
    /// Getter returns true when the selected task's id is in the boost set.
    /// Setter adds or removes the id from the boost set and persists to config.
    /// </summary>
    public bool SelectedTaskBoostsTurns
    {
        get => SelectedTask is not null && _boostedTaskIds.Contains(SelectedTask.Id);
        set
        {
            if (SelectedTask is null || string.IsNullOrEmpty(RootPath))
                return;

            var changed = value
                ? _boostedTaskIds.Add(SelectedTask.Id)
                : _boostedTaskIds.Remove(SelectedTask.Id);

            if (changed)
            {
                RelayConfigWriter.SetTurnBoost(RootPath, SelectedTask.Id, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(TurnBudgetLabel));
            }
        }
    }

    /// <summary>
    /// Human-readable label showing the effect of the toggle: e.g.
    /// "10× turn budget (200 → 2000)". Empty when no task is selected or the
    /// repo is uninitialized.
    /// </summary>
    public string TurnBudgetLabel =>
        SelectedTask is not null && !string.IsNullOrEmpty(RootPath)
            ? $"10× turn budget ({_maxTurns} → {_maxTurns * 10})"
            : string.Empty;

    /// <summary>
    /// Whether the turn-budget toggle can be interacted with. False when no
    /// task is selected or the repo isn't initialized.
    /// </summary>
    public bool CanToggleTurnBudget =>
        SelectedTask is not null && !string.IsNullOrEmpty(RootPath) && !IsBusy;

    /// <summary>
    /// Hydrates the boost set and MaxTurns from config. Called from
    /// <see cref="ReloadTaskListAsync"/> after config is loaded.
    /// </summary>
    internal void HydrateTurnBudget(RelayConfig config)
    {
        _boostedTaskIds = new HashSet<string>(config.BoostTurnsTaskIds ?? [], StringComparer.Ordinal);
        _maxTurns = config.MaxTurns;
        OnPropertyChanged(nameof(SelectedTaskBoostsTurns));
        OnPropertyChanged(nameof(TurnBudgetLabel));
    }
}
