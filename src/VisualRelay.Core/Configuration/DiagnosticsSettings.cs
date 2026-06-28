namespace VisualRelay.Core.Configuration;

/// <summary>
/// Per-machine (global) Visual Relay diagnostics preference, stored in the
/// user-level <c>.env</c> at <c>$XDG_CONFIG_HOME/visual-relay/.env</c> (falling
/// back to <c>$HOME/.config/visual-relay/.env</c>) via <see cref="KeyEnvFile"/>.
/// It is an APP/global preference — deliberately NOT in the per-repo
/// <c>.relay/config.json</c> — so it follows the machine, not the checkout.
///
/// Today it carries a single "verbose diagnostics" toggle whose only effect is
/// whether the nono sandbox wrapper is invoked with <c>--silent</c> (quiet, the
/// default) or not (verbose, for debugging the sandbox / Visual Relay itself).
/// The name is intentionally generic so further diagnostic-verbosity preferences
/// can join it later. The engine never reads this type — the app/CLI loads the
/// bool here and hands it to the runners, keeping the engine general-purpose.
///
/// Resolution precedence matches the rest of the settings layer: process env &gt;
/// user-level <c>.env</c>. Default is QUIET (<c>false</c>).
/// </summary>
public static class DiagnosticsSettings
{
    /// <summary>The user-level .env key backing the verbose-diagnostics preference.</summary>
    private const string VerboseDiagnosticsKey = "VR_VERBOSE_DIAGNOSTICS";

    /// <summary>
    /// Reads the verbose-diagnostics preference (process env wins over the user
    /// <c>.env</c> file). Returns <c>false</c> (quiet) when unset or unparseable so
    /// a missing/garbled value can never silently turn diagnostics verbose.
    /// </summary>
    public static bool LoadVerboseDiagnostics(IEnvironmentAccessor? accessor = null)
    {
        var raw = KeyEnvFile.GetEnv(VerboseDiagnosticsKey, accessor)
            ?? KeyEnvFile.Read(accessor).GetValueOrDefault(VerboseDiagnosticsKey);
        return bool.TryParse(raw, out var value) && value;
    }

    /// <summary>
    /// Persists the verbose-diagnostics preference to the user-level <c>.env</c>
    /// (creating the dir <c>0700</c> / file <c>0600</c> via <see cref="KeyEnvFile.Upsert"/>).
    /// Best-effort: when there is nowhere to write (HOME unresolved) the call is a no-op.
    /// </summary>
    public static void SaveVerboseDiagnostics(bool value, IEnvironmentAccessor? accessor = null)
    {
        try
        {
            KeyEnvFile.Upsert(VerboseDiagnosticsKey, value ? "true" : "false", accessor);
        }
        catch (InvalidOperationException)
        {
            // Nowhere to save (no resolvable config dir) — bail, mirroring ObsidianBridgeSettings.
        }
    }
}
