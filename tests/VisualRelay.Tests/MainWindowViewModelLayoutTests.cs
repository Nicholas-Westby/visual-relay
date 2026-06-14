using VisualRelay.App.ViewModels;

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
        Assert.False(vm.IsFocused); // not all collapsed

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
        Assert.Equal("⤢", vm.FocusButtonIcon);
        Assert.Equal("Collapse all surrounding panels to maximize the task detail", vm.FocusButtonTooltip);

        vm.ToggleFocusCommand.Execute(null);
        Assert.True(vm.IsFocused);
        Assert.Equal("Restore panels", vm.FocusButtonLabel);
        Assert.Equal("⤡", vm.FocusButtonIcon);
        Assert.Equal("Restore all panels to their previous layout", vm.FocusButtonTooltip);

        // Partial collapse: still shows "Focus task".
        vm.ToggleFocusCommand.Execute(null);
        vm.ToggleQueueCommand.Execute(null);
        Assert.False(vm.IsFocused);
        Assert.Equal("Focus task", vm.FocusButtonLabel);
        Assert.Equal("⤢", vm.FocusButtonIcon);
    }

    [Fact]
    public void PerPanelChevrons_ReflectCollapsedState()
    {
        var vm = new MainWindowViewModel();

        Assert.Equal("◀", vm.QueueChevron);
        Assert.Equal("◀", vm.StagesChevron);
        Assert.Equal("◀", vm.RunLogChevron);
        Assert.Equal("◀", vm.LlmCommandsChevron);

        vm.ToggleQueueCommand.Execute(null);
        Assert.Equal("▶", vm.QueueChevron);
        Assert.Equal("◀", vm.StagesChevron);

        vm.ToggleStagesCommand.Execute(null);
        Assert.Equal("▶", vm.StagesChevron);

        vm.ToggleRunLogCommand.Execute(null);
        Assert.Equal("▶", vm.RunLogChevron);

        vm.ToggleLlmCommandsCommand.Execute(null);
        Assert.Equal("▶", vm.LlmCommandsChevron);
    }
}
