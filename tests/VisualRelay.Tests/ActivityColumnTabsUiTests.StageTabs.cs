using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

public sealed partial class ActivityColumnTabsUiTests
{
    /// <summary>
    /// With no stage selected (StageDetail in NoStage state) the three
    /// stage-scoped tabs each display the same placeholder message telling
    /// the user to click a stage.
    /// </summary>
    [AvaloniaFact]
    public void NoStageSelected_StageTabsShowClickMessage()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal(StageDetailState.NoStage, vm.StageDetail.SystemState);
        Assert.Equal(StageDetailState.NoStage, vm.StageDetail.InputState);
        Assert.Equal(StageDetailState.NoStage, vm.StageDetail.OutputState);

        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);

        const string expected = "Click a stage to see its system prompt, input prompt, and output.";

        var systemView = SwitchToTabAndFindView<StageSystemView>(activityColumn, 2);
        AssertContainsText(systemView, expected);

        var inputView = SwitchToTabAndFindView<StageInputView>(activityColumn, 3);
        AssertContainsText(inputView, expected);

        var outputView = SwitchToTabAndFindView<StageOutputView>(activityColumn, 4);
        AssertContainsText(outputView, expected);
    }

    /// <summary>
    /// When the stage's system prompt is loaded and SystemState is Ready,
    /// the System tab shows the prompt text.
    /// </summary>
    [AvaloniaFact]
    public void SystemPromptReady_SystemTabShowsPromptText()
    {
        var vm = new MainWindowViewModel();
        vm.StageDetail.Header = "Stage 01 (Ideate)";
        vm.StageDetail.SystemState = StageDetailState.Ready;
        vm.StageDetail.SystemPromptText = "You are a senior software engineer. Frame the task.";

        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);

        var systemView = SwitchToTabAndFindView<StageSystemView>(activityColumn, 2);
        AssertContainsText(systemView, "You are a senior software engineer. Frame the task.");
        AssertContainsText(systemView, "Stage 01 (Ideate)");
    }

    /// <summary>
    /// When the input hasn't been generated yet (InputState == NotStarted),
    /// the Input tab shows a transitional message with the stage header.
    /// </summary>
    [AvaloniaFact]
    public void NotStartedStage_InputTabShowsTransitionalMessage()
    {
        var vm = new MainWindowViewModel();
        vm.StageDetail.Header = "Stage 03 (Diagnose)";
        vm.StageDetail.InputState = StageDetailState.NotStarted;

        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);

        var inputView = SwitchToTabAndFindView<StageInputView>(activityColumn, 3);
        AssertContainsText(inputView, "Stage 03 (Diagnose)");
        AssertContainsText(inputView, "will appear once the stage starts");
    }

    /// <summary>
    /// When the output hasn't completed yet (OutputState == NotComplete),
    /// the Output tab shows a transitional message with the stage header.
    /// </summary>
    [AvaloniaFact]
    public void NotCompleteStage_OutputTabShowsTransitionalMessage()
    {
        var vm = new MainWindowViewModel();
        vm.StageDetail.Header = "Stage 04 (Implement)";
        vm.StageDetail.OutputState = StageDetailState.NotComplete;

        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);

        var outputView = SwitchToTabAndFindView<StageOutputView>(activityColumn, 4);
        AssertContainsText(outputView, "Stage 04 (Implement)");
        AssertContainsText(outputView, "will appear once the stage completes");
    }

    /// <summary>
    /// Driver stages (e.g. Commit) run git directly — no LLM prompt or output.
    /// All three stage tabs show the driver message.
    /// </summary>
    [AvaloniaFact]
    public void DriverStage_AllThreeTabsShowDriverMessage()
    {
        var vm = new MainWindowViewModel();
        vm.StageDetail.Header = "Stage 12 (Commit)";
        vm.StageDetail.SystemState = StageDetailState.DriverStage;
        vm.StageDetail.InputState = StageDetailState.DriverStage;
        vm.StageDetail.OutputState = StageDetailState.DriverStage;

        var window = new MainWindow
        {
            DataContext = vm,
            Width = 1440,
            Height = 900
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var activityColumn = window.FindControl<ActivityColumn>("ActivityColumn");
        Assert.NotNull(activityColumn);

        const string driverMsg = "This stage runs git directly";

        var systemView = SwitchToTabAndFindView<StageSystemView>(activityColumn, 2);
        AssertContainsText(systemView, driverMsg);
        AssertContainsText(systemView, "Stage 12 (Commit)");

        var inputView = SwitchToTabAndFindView<StageInputView>(activityColumn, 3);
        AssertContainsText(inputView, driverMsg);

        var outputView = SwitchToTabAndFindView<StageOutputView>(activityColumn, 4);
        AssertContainsText(outputView, driverMsg);
    }
}
