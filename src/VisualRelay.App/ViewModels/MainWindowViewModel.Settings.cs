using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;

namespace VisualRelay.App.ViewModels;

public partial class MainWindowViewModel
{
    /// <summary>
    /// When true (default), the proof files under .relay/&lt;taskId&gt;/
    /// (ledger.md, &lt;taskId&gt;.seals, manifest.txt, status.json, and per-stage
    /// .input.json/.report.json — final attempt only) are force-added to each
    /// relay commit. When false, the proof files are still written to disk but
    /// omitted from commits. Task retirement records are always committed.
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

    /// <summary>
    /// GLOBAL (per-machine) "verbose diagnostics" preference. Default OFF = quiet:
    /// the nono sandbox wrapper runs with <c>--silent</c> so only the child command's
    /// output is captured. Turning it ON shows nono's full banner/footer diagnostics
    /// for debugging the sandbox or Visual Relay itself. OUTPUT-ONLY — it never weakens
    /// the sandbox. Threaded into the engine's runners as a plain bool at run launch
    /// (see <see cref="RunOneAsync"/> / <see cref="DrainQueueAsync"/>); the engine never
    /// reads app settings. Persisted to the user-level <c>.env</c>, NOT
    /// <c>.relay/config.json</c>, because it follows the machine, not the repo.
    /// </summary>
    [ObservableProperty]
    private bool _verboseSandboxDiagnostics;

    /// <summary>Persist the global toggle to the per-machine user <c>.env</c>.</summary>
    partial void OnVerboseSandboxDiagnosticsChanged(bool value) =>
        DiagnosticsSettings.SaveVerboseDiagnostics(value, EnvironmentAccessor);

    /// <summary>
    /// Hydrates <see cref="VerboseSandboxDiagnostics"/> from the per-machine user
    /// <c>.env</c>. Best-effort: a read hiccup leaves the quiet default. Called from
    /// <see cref="LoadInitialAsync"/> alongside the other settings loads.
    /// </summary>
    private void LoadDiagnosticsSettings()
    {
        try
        {
            VerboseSandboxDiagnostics = DiagnosticsSettings.LoadVerboseDiagnostics(EnvironmentAccessor);
        }
        catch
        {
            // Best-effort: leave the quiet default.
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

    [RelayCommand]
    private void RevealSettingsFile()
    {
        var path = KeyEnvFile.ResolvePathForCurrentUser(EnvironmentAccessor);
        if (File.Exists(path))
        {
            FileReveal.Reveal(path);
            return;
        }

        // If no key has ever been saved, .env and its parent directory don't
        // exist yet. Create the config directory (0700 to match Upsert since
        // .env holds API keys) so the OS file manager opens a real location.
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        FileReveal.Reveal(dir);
    }
}
