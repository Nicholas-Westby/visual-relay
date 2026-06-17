using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.Core.Init;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// When true, the nono OS-level sandbox is bypassed and Swival runs with
    /// unguarded file-system access. Defaults to false (sandbox ON).
    /// </summary>
    [ObservableProperty]
    private bool _bypassSandbox;

    /// <summary>Persist every toggle to .relay/config.json.</summary>
    partial void OnBypassSandboxChanged(bool value)
    {
        if (Directory.Exists(RootPath))
        {
            RelayConfigWriter.UpsertBypassSandbox(RootPath, value);
        }
    }

    /// <summary>
    /// When true (default), the four proof files under .relay/&lt;taskId&gt;/
    /// (ledger.md, &lt;taskId&gt;.seals, manifest.txt, status.json) are force-added
    /// to each relay commit. When false, the proof files are still written to
    /// disk but omitted from commits. Task retirement records are always committed.
    /// </summary>
    [ObservableProperty]
    private bool _commitProofArtifacts = true;

    /// <summary>Persist every toggle to .relay/config.json.</summary>
    partial void OnCommitProofArtifactsChanged(bool value)
    {
        if (Directory.Exists(RootPath))
        {
            RelayConfigWriter.UpsertCommitProofArtifacts(RootPath, value);
        }
    }

    /// <summary>Whether the settings modal dialog is currently open.</summary>
    [ObservableProperty]
    private bool _isSettingsOpen;

    /// <summary>
    /// Marks the settings modal as open and refreshes provider-key state so the
    /// dialog shows the current values. Called by the view when it opens the
    /// <see cref="Views.SettingsWindow"/>; closing is mirrored by
    /// <see cref="CloseSettings"/>.
    /// </summary>
    public async Task OpenSettingsAsync()
    {
        IsSettingsOpen = true;
        await RefreshKeyStatesAsync();
    }

    /// <summary>Marks the settings modal as closed (mirrors the window closing).</summary>
    public void CloseSettings() => IsSettingsOpen = false;
}
