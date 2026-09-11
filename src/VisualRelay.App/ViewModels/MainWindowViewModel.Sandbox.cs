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
    /// One-line reads/writes summary shown above the path lists, carried by the
    /// inspection result. The same sentence on every platform: nono enforces the
    /// blocked paths on macOS, Linux and, inside the WSL distro, Windows. Null
    /// when the info is unavailable.
    /// </summary>
    [ObservableProperty]
    private string? _sandboxReadsSummary;

    /// <summary>
    /// Captures the in-flight sandbox inspection so tests can
    /// <c>await viewModel.LastSandboxInspection</c> instead of polling
    /// <see cref="IsSandboxInfoLoading"/> on a wall-clock budget. The
    /// inspection spawns one nono subprocess per inherited group, so how long
    /// it takes is the machine's business and no poll budget can bound it.
    /// Set by <see cref="StartBackgroundInspections"/>; null until it is called.
    /// </summary>
    internal Task? LastSandboxInspection { get; private set; }


    /// <summary>
    /// Fires the background sandbox-path discovery without blocking the UI.
    /// Called ONLY from App startup (never the ctor or LoadInitialAsync) so unit
    /// tests spin no subprocesses. The handle is kept rather than discarded into
    /// <c>_</c> so tests can await it; the operation swallows its own faults.
    /// </summary>
    public void StartBackgroundInspections()
    {
        LastSandboxInspection = LoadSandboxPathsAsync();
    }

    /// <summary>
    /// Fires the async sandbox-path inspection without blocking the UI.
    /// Called from <see cref="StartBackgroundInspections"/> as a fire-and-forget;
    /// the nono group calls are subprocesses and must not hold up opening
    /// the Settings panel.
    /// </summary>
    private async Task LoadSandboxPathsAsync()
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
            SandboxReadsSummary = result.ReadsSummary;

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
            SandboxReadsSummary = null;
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
