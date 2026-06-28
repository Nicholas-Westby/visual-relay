using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Part A: the Settings-screen "Verbose sandbox diagnostics" toggle on the VM.
/// Default OFF (quiet); flipping it persists to the per-machine user <c>.env</c>
/// via <see cref="DiagnosticsSettings"/>, and it hydrates from there on load.
/// </summary>
public sealed class MainWindowViewModelDiagnosticsTests : IDisposable
{
    private readonly DictionaryEnvironmentAccessor _env = new();
    private readonly string _tempHome;

    public MainWindowViewModelDiagnosticsTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), "vr-vm-diagnostics-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempHome);
        _env["HOME"] = _tempHome;
        _env["XDG_CONFIG_HOME"] = "";
    }

    public void Dispose() => TestFileSystem.DeleteDirectoryResilient(_tempHome);

    [Fact]
    public void VerboseSandboxDiagnostics_DefaultsToFalse()
    {
        var vm = new MainWindowViewModel { EnvironmentAccessor = _env };
        Assert.False(vm.VerboseSandboxDiagnostics);
    }

    [Fact]
    public void SettingVerboseSandboxDiagnostics_PersistsToUserEnv()
    {
        // ReSharper disable once UseObjectOrCollectionInitializer — exercise the SETTER's
        // persist side effect, which an initializer would bypass.
        var vm = new MainWindowViewModel { EnvironmentAccessor = _env };

        vm.VerboseSandboxDiagnostics = true;

        Assert.True(DiagnosticsSettings.LoadVerboseDiagnostics(_env));
    }

    [Fact]
    public async Task LoadInitialAsync_HydratesVerboseSandboxDiagnosticsFromUserEnv()
    {
        DiagnosticsSettings.SaveVerboseDiagnostics(true, _env);
        using var repo = TestRepository.Create();

        var vm = new MainWindowViewModel { RootPath = repo.Root, EnvironmentAccessor = _env };
        await vm.LoadInitialAsync();

        Assert.True(vm.VerboseSandboxDiagnostics);
    }
}
