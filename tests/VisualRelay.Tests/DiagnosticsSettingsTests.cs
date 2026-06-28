using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Part A: the GLOBAL/per-machine "verbose diagnostics" preference, persisted to
/// the user-level .env via <see cref="KeyEnvFile"/> (NOT per-repo .relay/config.json).
/// Default is quiet (false). Mirrors the <c>ObsidianBridgeSettings</c> pattern:
/// process-env-wins over the file, best-effort save, sandboxed HOME/XDG in tests.
/// </summary>
public sealed class DiagnosticsSettingsTests : IDisposable
{
    private readonly DictionaryEnvironmentAccessor _env = new();
    private readonly string _tempHome;

    public DiagnosticsSettingsTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "vr-diagnostics-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
        _env["HOME"] = _tempHome;
        _env["XDG_CONFIG_HOME"] = "";
    }

    public void Dispose() => TestFileSystem.DeleteDirectoryResilient(_tempHome);

    [Fact]
    public void LoadVerboseDiagnostics_NoEnvFile_DefaultsToFalse()
    {
        Assert.False(DiagnosticsSettings.LoadVerboseDiagnostics(_env));
    }

    [Fact]
    public void SaveTrue_ThenLoad_ReturnsTrue()
    {
        DiagnosticsSettings.SaveVerboseDiagnostics(true, _env);
        Assert.True(DiagnosticsSettings.LoadVerboseDiagnostics(_env));
    }

    [Fact]
    public void SaveFalse_ThenLoad_ReturnsFalse()
    {
        DiagnosticsSettings.SaveVerboseDiagnostics(true, _env);
        DiagnosticsSettings.SaveVerboseDiagnostics(false, _env);
        Assert.False(DiagnosticsSettings.LoadVerboseDiagnostics(_env));
    }

    [Fact]
    public void ProcessEnv_WinsOverFile()
    {
        // File says false, but the process env explicitly enables verbose → env wins.
        DiagnosticsSettings.SaveVerboseDiagnostics(false, _env);
        _env["VR_VERBOSE_DIAGNOSTICS"] = "true";
        Assert.True(DiagnosticsSettings.LoadVerboseDiagnostics(_env));
    }
}
