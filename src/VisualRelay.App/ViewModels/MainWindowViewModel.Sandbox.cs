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
