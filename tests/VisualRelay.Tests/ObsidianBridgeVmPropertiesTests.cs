using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// Simple property-default tests for the Obsidian bridge VM integration.
/// Split from <see cref="ObsidianBridgeVmTests"/> to stay under the 300-line guard.
/// </summary>
[Collection("Headless")]
public sealed class ObsidianBridgeVmPropertiesTests
{
    [AvaloniaFact]
    public void ObsidianEnabled_DefaultsToFalse()
    {
        var viewModel = new MainWindowViewModel();
        Assert.False(viewModel.ObsidianEnabled);
    }

    [AvaloniaFact]
    public void ObsidianVaultRoot_DefaultsToEmpty()
    {
        var viewModel = new MainWindowViewModel();
        Assert.Equal(string.Empty, viewModel.ObsidianVaultRoot);
    }

    [AvaloniaFact]
    public void ObsidianPollSeconds_DefaultsToSixty()
    {
        var viewModel = new MainWindowViewModel();
        Assert.Equal(60, viewModel.ObsidianPollSeconds);
    }
}
