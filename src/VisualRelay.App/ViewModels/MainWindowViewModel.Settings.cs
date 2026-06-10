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
}
