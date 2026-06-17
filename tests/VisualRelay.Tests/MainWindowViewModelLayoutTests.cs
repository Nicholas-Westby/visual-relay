using VisualRelay.App.ViewModels;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

public sealed class MainWindowViewModelLayoutTests
{
    [Fact]
    public void AllCollapseFlagsDefaultToFalse()
    {
        var vm = new MainWindowViewModel();

        Assert.False(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        Assert.False(vm.IsRunLogCollapsed);
        Assert.False(vm.IsLlmCommandsCollapsed);
        Assert.False(vm.IsFocused);
        Assert.False(vm.IsActivityColumnCollapsed);
    }

    [Fact]
    public void PerPanelToggleCommands_FlipOnlyTheirFlag()
    {
        var vm = new MainWindowViewModel();

        // Queue
        vm.ToggleQueueCommand.Execute(null);
        Assert.True(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        Assert.False(vm.IsRunLogCollapsed);
        Assert.False(vm.IsLlmCommandsCollapsed);
        Assert.False(vm.IsFocused);
        vm.ToggleQueueCommand.Execute(null);
        Assert.False(vm.IsQueueCollapsed);

        // Stages
        vm.ToggleStagesCommand.Execute(null);
        Assert.True(vm.IsStagesCollapsed);
        Assert.False(vm.IsQueueCollapsed);
        Assert.False(vm.IsRunLogCollapsed);
        Assert.False(vm.IsLlmCommandsCollapsed);
        vm.ToggleStagesCommand.Execute(null);
        Assert.False(vm.IsStagesCollapsed);

        // Run Log
        vm.ToggleRunLogCommand.Execute(null);
        Assert.True(vm.IsRunLogCollapsed);
        Assert.False(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        Assert.False(vm.IsLlmCommandsCollapsed);
        vm.ToggleRunLogCommand.Execute(null);
        Assert.False(vm.IsRunLogCollapsed);

        // LLM Commands
        vm.ToggleLlmCommandsCommand.Execute(null);
        Assert.True(vm.IsLlmCommandsCollapsed);
        Assert.False(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        Assert.False(vm.IsRunLogCollapsed);
        vm.ToggleLlmCommandsCommand.Execute(null);
        Assert.False(vm.IsLlmCommandsCollapsed);
    }

    [Fact]
    public void ToggleFocus_FromDefaultState_CollapsesAllFourAndSetsIsFocused()
    {
        var vm = new MainWindowViewModel();
        vm.ToggleFocusCommand.Execute(null);
        Assert.True(vm.IsQueueCollapsed);
        Assert.True(vm.IsStagesCollapsed);
        Assert.True(vm.IsRunLogCollapsed);
        Assert.True(vm.IsLlmCommandsCollapsed);
        Assert.True(vm.IsFocused);
        Assert.True(vm.IsActivityColumnCollapsed);
    }

    [Fact]
    public void ToggleFocus_Twice_RestoresExactPreFocusFlags()
    {
        var vm = new MainWindowViewModel();
        vm.ToggleFocusCommand.Execute(null);
        Assert.True(vm.IsFocused);
        vm.ToggleFocusCommand.Execute(null);
        Assert.False(vm.IsFocused);
        Assert.False(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        Assert.False(vm.IsRunLogCollapsed);
        Assert.False(vm.IsLlmCommandsCollapsed);
    }

    [Fact]
    public void ToggleFocus_AfterIndividualCollapse_RestoresThatOneCollapsed()
    {
        var vm = new MainWindowViewModel();
        // Pre-collapse just the Queue, then focus.
        vm.ToggleQueueCommand.Execute(null);
        Assert.True(vm.IsQueueCollapsed);
        Assert.False(vm.IsFocused);
        vm.ToggleFocusCommand.Execute(null);
        Assert.True(vm.IsFocused);
        Assert.True(vm.IsQueueCollapsed);
        Assert.True(vm.IsStagesCollapsed);
        Assert.True(vm.IsRunLogCollapsed);
        Assert.True(vm.IsLlmCommandsCollapsed);
        // Unfocus — only Queue should still be collapsed.
        vm.ToggleFocusCommand.Execute(null);
        Assert.False(vm.IsFocused);
        Assert.True(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        Assert.False(vm.IsRunLogCollapsed);
        Assert.False(vm.IsLlmCommandsCollapsed);
    }

    [Fact]
    public void FocusSnapshot_HandlesManualChangesWhileFocused()
    {
        var vm = new MainWindowViewModel();
        // Focus from default.
        vm.ToggleFocusCommand.Execute(null);
        Assert.True(vm.IsFocused);
        // Manually expand Queue while focused.
        vm.ToggleQueueCommand.Execute(null);
        Assert.False(vm.IsQueueCollapsed);
        Assert.False(vm.IsFocused);
        // Focus again — snapshot is now {Queue=false, rest=true}.
        vm.ToggleFocusCommand.Execute(null);
        Assert.True(vm.IsFocused);
        Assert.True(vm.IsQueueCollapsed);
        Assert.True(vm.IsStagesCollapsed);
        Assert.True(vm.IsRunLogCollapsed);
        Assert.True(vm.IsLlmCommandsCollapsed);
        // Unfocus restores: Queue was NOT collapsed before focus.
        vm.ToggleFocusCommand.Execute(null);
        Assert.False(vm.IsFocused);
        Assert.False(vm.IsQueueCollapsed);
        Assert.True(vm.IsStagesCollapsed);
        Assert.True(vm.IsRunLogCollapsed);
        Assert.True(vm.IsLlmCommandsCollapsed);
    }

    [Fact]
    public void IsActivityColumnCollapsed_TrueOnlyWhenBothRightFlagsSet()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.IsActivityColumnCollapsed);
        // Only run log collapsed.
        vm.ToggleRunLogCommand.Execute(null);
        Assert.False(vm.IsActivityColumnCollapsed);
        // Only LLM commands collapsed.
        vm.ToggleRunLogCommand.Execute(null);
        vm.ToggleLlmCommandsCommand.Execute(null);
        Assert.False(vm.IsActivityColumnCollapsed);
        // Both collapsed.
        vm.ToggleRunLogCommand.Execute(null);
        Assert.True(vm.IsActivityColumnCollapsed);
    }

    [Fact]
    public void IsFocused_TrueOnlyWhenAllFourFlagsSet()
    {
        var vm = new MainWindowViewModel();
        Assert.False(vm.IsFocused);
        vm.IsQueueCollapsed = true;
        Assert.False(vm.IsFocused);
        vm.IsStagesCollapsed = true;
        Assert.False(vm.IsFocused);
        vm.IsRunLogCollapsed = true;
        Assert.False(vm.IsFocused);
        vm.IsLlmCommandsCollapsed = true;
        Assert.True(vm.IsFocused);
        vm.IsQueueCollapsed = false;
        Assert.False(vm.IsFocused);
    }

    [Fact]
    public void FocusButtonLabels_ReflectFocusedState()
    {
        var vm = new MainWindowViewModel();
        Assert.Equal("Focus task", vm.FocusButtonLabel);
        Assert.Equal("Collapse all surrounding panels to maximize the task detail", vm.FocusButtonTooltip);
        vm.ToggleFocusCommand.Execute(null);
        Assert.True(vm.IsFocused);
        Assert.Equal("Restore panels", vm.FocusButtonLabel);
        Assert.Equal("Restore all panels to their previous layout", vm.FocusButtonTooltip);
        // Partial collapse: still shows "Focus task".
        vm.ToggleFocusCommand.Execute(null);
        vm.ToggleQueueCommand.Execute(null);
        Assert.False(vm.IsFocused);
        Assert.Equal("Focus task", vm.FocusButtonLabel);
    }

    [Fact]
    public void Chevrons_FollowDirectionAndAxisScheme()
    {
        var vm = new MainWindowViewModel();
        // Queue (left edge): header Left expanded (collapse left), Right collapsed.
        Assert.Equal(ChevronDirection.Left, vm.QueueChevron);
        Assert.Equal(ChevronDirection.Right, vm.QueueRailChevron);
        vm.IsQueueCollapsed = true;
        Assert.Equal(ChevronDirection.Right, vm.QueueChevron);
        Assert.Equal(ChevronDirection.Right, vm.QueueRailChevron);
        vm.IsQueueCollapsed = false;
        // Stages (vertical fold): Down expanded, Right collapsed.
        Assert.Equal(ChevronDirection.Down, vm.StagesChevron);
        vm.IsStagesCollapsed = true;
        Assert.Equal(ChevronDirection.Right, vm.StagesChevron);
        vm.IsStagesCollapsed = false;
        Assert.Equal(ChevronDirection.Down, vm.StagesChevron);
        // Run Log: sibling open -> vertical Down/Right; both collapsed -> Right slide.
        Assert.Equal(ChevronDirection.Down, vm.RunLogChevron);
        vm.IsRunLogCollapsed = true;
        Assert.Equal(ChevronDirection.Right, vm.RunLogChevron);
        vm.IsRunLogCollapsed = false;
        vm.IsLlmCommandsCollapsed = true;
        Assert.Equal(ChevronDirection.Down, vm.RunLogChevron); // sibling collapsed, column NOT collapsed -> fold in place
        vm.IsRunLogCollapsed = true;
        Assert.Equal(ChevronDirection.Right, vm.RunLogChevron); // both collapsed -> column-to-rail slide
        vm.IsLlmCommandsCollapsed = false;
        vm.IsRunLogCollapsed = false;
        // LLM Commands: same dual-mode pattern.
        Assert.Equal(ChevronDirection.Down, vm.LlmCommandsChevron);
        vm.IsLlmCommandsCollapsed = true;
        Assert.Equal(ChevronDirection.Right, vm.LlmCommandsChevron);
        vm.IsLlmCommandsCollapsed = false;
        vm.IsRunLogCollapsed = true;
        Assert.Equal(ChevronDirection.Down, vm.LlmCommandsChevron); // sibling collapsed, column NOT collapsed -> fold in place
        vm.IsLlmCommandsCollapsed = true;
        Assert.Equal(ChevronDirection.Right, vm.LlmCommandsChevron); // both collapsed -> column-to-rail slide
        vm.IsRunLogCollapsed = false;
        vm.IsLlmCommandsCollapsed = false;
        // Activity rail: always Left (expand left from right edge).
        Assert.Equal(ChevronDirection.Left, vm.ActivityRailChevron);
        // Header tooltips flip.
        Assert.Equal("Collapse Queue", vm.QueueHeaderTooltip);
        vm.IsQueueCollapsed = true;
        Assert.Equal("Expand Queue", vm.QueueHeaderTooltip);
        vm.IsQueueCollapsed = false;
        Assert.Equal("Collapse Stages", vm.StagesHeaderTooltip);
        vm.IsStagesCollapsed = true;
        Assert.Equal("Expand Stages", vm.StagesHeaderTooltip);
        vm.IsStagesCollapsed = false;
        Assert.Equal("Collapse Run Log", vm.RunLogHeaderTooltip);
        vm.IsRunLogCollapsed = true;
        Assert.Equal("Expand Run Log", vm.RunLogHeaderTooltip);
        vm.IsRunLogCollapsed = false;
        Assert.Equal("Collapse LLM Commands", vm.LlmCommandsHeaderTooltip);
        vm.IsLlmCommandsCollapsed = true;
        Assert.Equal("Expand LLM Commands", vm.LlmCommandsHeaderTooltip);
    }

    [Fact]
    public void ChevronPropertyChanged_FiresOnFlagChange()
    {
        var vm = new MainWindowViewModel();
        var received = new List<string>();
        vm.PropertyChanged += (_, e) => received.Add(e.PropertyName!);

        received.Clear();
        vm.IsQueueCollapsed = true;
        Assert.Contains(nameof(MainWindowViewModel.QueueChevron), received);
        Assert.Contains(nameof(MainWindowViewModel.QueueRailChevron), received);
        Assert.Contains(nameof(MainWindowViewModel.QueueHeaderTooltip), received);

        received.Clear();
        vm.IsStagesCollapsed = true;
        Assert.Contains(nameof(MainWindowViewModel.StagesChevron), received);
        Assert.Contains(nameof(MainWindowViewModel.StagesHeaderTooltip), received);

        received.Clear();
        vm.IsRunLogCollapsed = true;
        Assert.Contains(nameof(MainWindowViewModel.RunLogChevron), received);
        Assert.Contains(nameof(MainWindowViewModel.RunLogHeaderTooltip), received);
        Assert.Contains(nameof(MainWindowViewModel.LlmCommandsChevron), received);

        received.Clear();
        vm.IsLlmCommandsCollapsed = true;
        Assert.Contains(nameof(MainWindowViewModel.LlmCommandsChevron), received);
        Assert.Contains(nameof(MainWindowViewModel.LlmCommandsHeaderTooltip), received);
        Assert.Contains(nameof(MainWindowViewModel.RunLogChevron), received);
    }
}
