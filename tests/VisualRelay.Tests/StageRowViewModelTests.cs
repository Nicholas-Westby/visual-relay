using CommunityToolkit.Mvvm.Input;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class StageRowViewModelTests
{
    [Fact]
    public void Constructor_StoresStageProperties()
    {
        var stage = new StageRowViewModel(RelayStages.All[0]);

        Assert.Equal(1, stage.Number);
        Assert.Equal("Ideate", stage.Name);
        Assert.Equal("cheap", stage.Tier);
        Assert.Equal("Waiting", stage.Status);
    }

    [Fact]
    public void SelectCommand_DefaultsToNull()
    {
        var stage = new StageRowViewModel(RelayStages.All[0]);

        Assert.Null(stage.SelectCommand);
    }

    [Fact]
    public void SelectCommand_StoresCommandWhenPassed()
    {
        var command = new RelayCommand<StageRowViewModel>(_ => { });
        var stage = new StageRowViewModel(RelayStages.All[0], command);

        Assert.Same(command, stage.SelectCommand);
    }

    [Fact]
    public void SelectCommand_CanBeNull()
    {
        var stage = new StageRowViewModel(RelayStages.All[0], null);

        Assert.Null(stage.SelectCommand);
    }

    [Fact]
    public void SelectCommand_IsIRelayCommandOfStageRowViewModel()
    {
        var command = new RelayCommand<StageRowViewModel>(_ => { });
        var stage = new StageRowViewModel(RelayStages.All[0], command);

        var selectCommand = stage.SelectCommand;

        Assert.NotNull(selectCommand);
        Assert.IsAssignableFrom<IRelayCommand<StageRowViewModel>>(selectCommand);
    }
}
