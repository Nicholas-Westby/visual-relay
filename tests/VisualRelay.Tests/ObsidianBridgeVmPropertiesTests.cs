using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Simple property-default tests for the Obsidian bridge VM integration.
/// Split from <see cref="ObsidianBridgeVmTests"/> to stay under the 300-line guard.
/// Tests that mutate a persisted property inject a sandboxed env accessor (temp
/// HOME) so saving never touches the user's real ~/.config config file.
/// </summary>
[Collection("Headless")]
public sealed class ObsidianBridgeVmPropertiesTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(),
        "vr-obsidian-vm-props", Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFileSystem.DeleteDirectoryResilient(_scratch);

    private MainWindowViewModel SandboxedViewModel()
    {
        var env = new DictionaryEnvironmentAccessor
        {
            ["HOME"] = Path.Combine(_scratch, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(_scratch, "xdg")
        };
        Directory.CreateDirectory(env["HOME"]!);
        return new MainWindowViewModel(environmentAccessor: env);
    }

    [AvaloniaFact]
    public void ObsidianEnabled_DefaultsToFalse()
    {
        var viewModel = SandboxedViewModel();
        Assert.False(viewModel.ObsidianEnabled);
    }

    [AvaloniaFact]
    public void ObsidianVaultRoot_DefaultsToEmpty()
    {
        var viewModel = SandboxedViewModel();
        Assert.Equal(string.Empty, viewModel.ObsidianVaultRoot);
    }

    [AvaloniaFact]
    public void ObsidianPollSeconds_DefaultsToSixty()
    {
        var viewModel = SandboxedViewModel();
        Assert.Equal(60, viewModel.ObsidianPollSeconds);
    }

    [AvaloniaFact]
    public void ObsidianPollSeconds_LiveSetBelowFloor_ClampsToMinimum()
    {
        // The ≥15 clamp must not be Load-only: a live set via the settings TextBox
        // or the control API would otherwise spin the bridge timer far too fast.
        var viewModel = SandboxedViewModel();
        viewModel.ObsidianPollSeconds = 3;

        Assert.Equal(ObsidianBridgeSettings.MinPollSeconds, viewModel.ObsidianPollSeconds);
    }

    [AvaloniaFact]
    public void ObsidianPollSeconds_LiveSetAtOrAboveFloor_Unchanged()
    {
        var viewModel = SandboxedViewModel();
        viewModel.ObsidianPollSeconds = 90;

        Assert.Equal(90, viewModel.ObsidianPollSeconds);
    }
}
