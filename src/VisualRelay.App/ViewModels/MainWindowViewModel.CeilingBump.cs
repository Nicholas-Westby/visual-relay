using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Init;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    // ── Ceiling timeout bump ──────────────────────────────────────────────

    /// <summary>True when the selected task's latest error mentions the absolute ceiling.</summary>
    public bool IsCeilingTimeoutError =>
        SelectedTaskError?.Contains("absolute ceiling", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Exposed for tests so they can assert CanExecute state.</summary>
    public bool CanBumpCeilingPublic => CanBumpCeiling();

    [RelayCommand(CanExecute = nameof(CanBumpCeiling))]
    private async Task BumpCeilingAsync()
    {
        IsBusy = true;
        try
        {
            var config = await RelayConfigLoader.LoadAsync(RootPath, CancellationToken.None);
            var newTimeout = config.SubagentTimeoutMilliseconds + 600_000;
            RelayConfigWriter.UpsertSubagentTimeout(RootPath, newTimeout);
            var minutes = newTimeout / 60_000;
            StatusText = $"Stage timeout raised to {minutes}m — applies from the next run.";
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't update timeout: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanBumpCeiling() =>
        IsCeilingTimeoutError
        && !IsBusy
        && !string.IsNullOrEmpty(RootPath);

    // Hook: when SelectedTaskError changes (set by OnSelectedTaskChanged in
    // RunHistory.cs and RefreshSelectedTaskErrorAfterRun in LiveState.cs),
    // re-evaluate IsCeilingTimeoutError so the banner's button visibility updates.
    partial void OnSelectedTaskErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(IsCeilingTimeoutError));
        BumpCeilingCommand.NotifyCanExecuteChanged();
    }
}
