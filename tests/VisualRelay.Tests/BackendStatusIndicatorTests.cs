using Avalonia.Media;
using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class BackendStatusIndicatorTests
{
    private static readonly Color Green = Color.Parse("#5AD47D");
    private static readonly Color Red = Color.Parse("#F36F63");

    [Fact]
    public void BackendStatusBrush_IsGreenWhenReachableAndRedWhenDown()
    {
        var viewModel = new MainWindowViewModel { IsBackendReachable = true };
        Assert.Equal(Green, ((ISolidColorBrush)viewModel.BackendStatusBrush).Color);

        viewModel.IsBackendReachable = false;
        Assert.Equal(Red, ((ISolidColorBrush)viewModel.BackendStatusBrush).Color);
    }

    [Fact]
    public void BackendStatusLabel_ShowsHostPortWhenReachableAndDownOtherwise()
    {
        var host = ModelBackend.BaseUrl.Replace("http://", string.Empty, StringComparison.Ordinal);
        var viewModel = new MainWindowViewModel { IsBackendReachable = true };
        Assert.Equal($"backend: {host}", viewModel.BackendStatusLabel);

        viewModel.IsBackendReachable = false;
        Assert.Equal("backend down", viewModel.BackendStatusLabel);
    }

    [Fact]
    public void StartBackendCommand_IsEnabledOnlyWhenBackendIsDown()
    {
        var viewModel = new MainWindowViewModel { IsBackendReachable = true };
        Assert.False(viewModel.StartBackendCommand.CanExecute(null));

        viewModel.IsBackendReachable = false;
        Assert.True(viewModel.StartBackendCommand.CanExecute(null));
    }
}
