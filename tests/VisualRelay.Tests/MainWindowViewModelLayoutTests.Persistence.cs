using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelLayoutTests
{
    [Fact]
    public void Ctor_ClampsTooSmallPersistedWidth_UpToMinimum()
    {
        // A stale ui-state.json with a collapsed width must not break layout.
        UiStateStore.Save(new UiState(ActivityColumnWidth: 5, ActivityTabIndex: 0), _env);

        var vm = CreateViewModel();

        Assert.Equal(MainWindowViewModel.MinActivityColumnWidth, vm.ActivityColumnWidth);
    }

    [Fact]
    public void Ctor_ClampsTooLargePersistedWidth_DownToMaximum()
    {
        UiStateStore.Save(new UiState(ActivityColumnWidth: 9999, ActivityTabIndex: 0), _env);

        var vm = CreateViewModel();

        Assert.Equal(MainWindowViewModel.MaxActivityColumnWidth, vm.ActivityColumnWidth);
    }

    [Fact]
    public void Ctor_KeepsInRangePersistedWidth_Unchanged()
    {
        UiStateStore.Save(new UiState(ActivityColumnWidth: 480, ActivityTabIndex: 0), _env);

        var vm = CreateViewModel();

        Assert.Equal(480, vm.ActivityColumnWidth);
    }
}
