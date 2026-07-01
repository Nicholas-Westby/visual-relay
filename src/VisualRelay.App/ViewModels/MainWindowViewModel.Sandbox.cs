using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty]
    private ObservableCollection<SandboxPathEntry> _sandboxReadablePaths = [];

    [ObservableProperty]
    private ObservableCollection<SandboxPathEntry> _sandboxWritablePaths = [];

    [ObservableProperty]
    private ObservableCollection<SandboxPathEntry> _sandboxBlockedPaths = [];

    [ObservableProperty]
    private bool _isSandboxInfoAvailable;

    [ObservableProperty]
    private bool _isSandboxInfoLoading;

    /// <summary>
    /// Fires the async sandbox-path inspection without blocking the UI.
    /// Called from <see cref="LoadInitialAsync"/> as a fire-and-forget;
    /// the nono group calls are subprocesses and must not hold up opening
    /// the Settings panel.
    /// </summary>
    internal async Task LoadSandboxPathsAsync()
    {
        IsSandboxInfoLoading = true;
        try
        {
            IReadOnlyList<string>? extraAllowPaths = null;
            try
            {
                if (Directory.Exists(RootPath))
                {
                    var config = await RelayConfigLoader.LoadAsync(RootPath);
                    extraAllowPaths = config.SandboxExtraAllowPaths;
                }
            }
            catch { /* best-effort — config may not exist yet */ }

            var result = await SandboxPathInspector.InspectAsync(
                workspaceRoot: Directory.Exists(RootPath) ? RootPath : null,
                extraAllowPaths: extraAllowPaths);

            IsSandboxInfoAvailable = result.IsAvailable;

            SandboxReadablePaths.Clear();
            SandboxWritablePaths.Clear();
            SandboxBlockedPaths.Clear();

            if (result.IsAvailable)
            {
                foreach (var e in result.ReadablePaths)
                    SandboxReadablePaths.Add(e);
                foreach (var e in result.WritablePaths)
                    SandboxWritablePaths.Add(e);
                foreach (var e in result.BlockedPaths)
                    SandboxBlockedPaths.Add(e);
            }
        }
        catch
        {
            IsSandboxInfoAvailable = false;
            SandboxReadablePaths.Clear();
            SandboxWritablePaths.Clear();
            SandboxBlockedPaths.Clear();
        }
        finally
        {
            IsSandboxInfoLoading = false;
        }
    }
}
