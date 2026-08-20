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
    /// Platform-aware one-line reads/writes summary shown above the path lists.
    /// Carried by the inspection result, so Windows (unrestricted reads) and
    /// macOS/Linux (reads minus the blocked paths) each get an honest sentence
    /// with no OS check here. Null when the info is unavailable.
    /// </summary>
    [ObservableProperty]
    private string? _sandboxReadsSummary;

    /// <summary>
    /// Windows-only caveat text against the credential denials (the MXC sandbox
    /// may not enforce them yet). Empty/null on macOS/Linux, so the caveat row is
    /// hidden there. See <see cref="SandboxWindowsCaveatUrl"/> for the tracker.
    /// </summary>
    [ObservableProperty]
    private string? _sandboxWindowsCaveat;

    /// <summary>Tracking link opened from the caveat row; null when no caveat.</summary>
    [ObservableProperty]
    private string? _sandboxWindowsCaveatUrl;

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
    /// Captures the in-flight startup backend probe for the same reason as
    /// <see cref="LastSandboxInspection"/>: the probe is an HTTP GET carrying
    /// its own 2s timeout, so tests await the real operation rather than bet a
    /// poll budget against it.
    /// </summary>
    internal Task? LastBackendStatusRefresh { get; private set; }

    /// <summary>
    /// Fires background inspections (sandbox-path discovery and an initial
    /// backend readiness probe) without blocking the UI. Called ONLY from
    /// App startup (never the ctor or LoadInitialAsync) so unit tests spin
    /// no subprocesses or sockets — exactly the same pattern as
    /// <see cref="StartBackendMonitoring"/>. Both handles are kept rather than
    /// discarded into <c>_</c> so tests can await them; nothing else changes
    /// (both operations already swallow their own faults).
    /// </summary>
    public void StartBackgroundInspections()
    {
        LastSandboxInspection = LoadSandboxPathsAsync();
        LastBackendStatusRefresh = RefreshBackendStatusAsync();
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
            SandboxWindowsCaveat = result.WindowsCredentialCaveat;
            SandboxWindowsCaveatUrl = result.WindowsCredentialCaveatUrl;

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
            SandboxWindowsCaveat = null;
            SandboxWindowsCaveatUrl = null;
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
