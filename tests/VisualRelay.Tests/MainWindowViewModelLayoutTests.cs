using VisualRelay.App.ViewModels;
using VisualRelay.App.Views.Controls;

namespace VisualRelay.Tests;

public sealed partial class MainWindowViewModelLayoutTests : IDisposable
{
    private readonly TestRepository _repo = TestRepository.Create();
    private readonly DictionaryEnvironmentAccessor _env = new();
    public MainWindowViewModelLayoutTests() => _env["XDG_CONFIG_HOME"] = _repo.Root;
    public void Dispose() => _repo.Dispose();
    private MainWindowViewModel CreateViewModel() => new(_env);

    [Fact]
    public void AllCollapseFlagsDefaultToFalse()
    {
        var vm = CreateViewModel();
        Assert.False(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        Assert.False(vm.IsActivityColumnCollapsed);
        Assert.False(vm.IsFocused);
    }

    [Fact]
    public void PerPanelToggleCommands_FlipOnlyTheirFlag()
    {
        var vm = CreateViewModel();

        // Queue
        vm.ToggleQueueCommand.Execute(null);
        Assert.True(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        Assert.False(vm.IsActivityColumnCollapsed);
        Assert.False(vm.IsFocused);
        vm.ToggleQueueCommand.Execute(null);
        Assert.False(vm.IsQueueCollapsed);

        // Stages
        vm.ToggleStagesCommand.Execute(null);
        Assert.True(vm.IsStagesCollapsed);
        Assert.False(vm.IsQueueCollapsed);
        Assert.False(vm.IsActivityColumnCollapsed);
        vm.ToggleStagesCommand.Execute(null);
        Assert.False(vm.IsStagesCollapsed);

        // Activity column
        vm.ToggleActivityColumnCommand.Execute(null);
        Assert.True(vm.IsActivityColumnCollapsed);
        Assert.False(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        vm.ToggleActivityColumnCommand.Execute(null);
        Assert.False(vm.IsActivityColumnCollapsed);
    }

    [Fact]
    public void ToggleFocus_FromDefaultState_CollapsesAllThreeAndSetsIsFocused()
    {
        var vm = CreateViewModel();
        vm.ToggleFocusCommand.Execute(null);
        Assert.True(vm.IsQueueCollapsed);
        Assert.True(vm.IsStagesCollapsed);
        Assert.True(vm.IsActivityColumnCollapsed);
        Assert.True(vm.IsFocused);
    }

    [Fact]
    public void ToggleFocus_Twice_RestoresExactPreFocusFlags()
    {
        var vm = CreateViewModel();
        vm.ToggleFocusCommand.Execute(null);
        Assert.True(vm.IsFocused);
        vm.ToggleFocusCommand.Execute(null);
        Assert.False(vm.IsFocused);
        Assert.False(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        Assert.False(vm.IsActivityColumnCollapsed);
    }

    [Fact]
    public void ToggleFocus_AfterIndividualCollapse_RestoresThatOneCollapsed()
    {
        var vm = CreateViewModel();
        // Pre-collapse just the Queue, then focus.
        vm.ToggleQueueCommand.Execute(null);
        Assert.True(vm.IsQueueCollapsed);
        Assert.False(vm.IsFocused);
        vm.ToggleFocusCommand.Execute(null);
        Assert.True(vm.IsFocused);
        Assert.True(vm.IsQueueCollapsed);
        Assert.True(vm.IsStagesCollapsed);
        Assert.True(vm.IsActivityColumnCollapsed);
        // Unfocus — only Queue should still be collapsed.
        vm.ToggleFocusCommand.Execute(null);
        Assert.False(vm.IsFocused);
        Assert.True(vm.IsQueueCollapsed);
        Assert.False(vm.IsStagesCollapsed);
        Assert.False(vm.IsActivityColumnCollapsed);
    }

    [Fact]
    public void FocusSnapshot_HandlesManualChangesWhileFocused()
    {
        var vm = CreateViewModel();
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
        Assert.True(vm.IsActivityColumnCollapsed);
        // Unfocus restores: Queue was NOT collapsed before focus.
        vm.ToggleFocusCommand.Execute(null);
        Assert.False(vm.IsFocused);
        Assert.False(vm.IsQueueCollapsed);
        Assert.True(vm.IsStagesCollapsed);
        Assert.True(vm.IsActivityColumnCollapsed);
    }

    [Fact]
    public void IsActivityColumnCollapsed_TogglesDirectly()
    {
        var vm = CreateViewModel();
        Assert.False(vm.IsActivityColumnCollapsed);
        vm.ToggleActivityColumnCommand.Execute(null);
        Assert.True(vm.IsActivityColumnCollapsed);
        vm.ToggleActivityColumnCommand.Execute(null);
        Assert.False(vm.IsActivityColumnCollapsed);
    }

    [Fact]
    public void IsFocused_TrueOnlyWhenAllThreeFlagsSet()
    {
        var vm = CreateViewModel();
        Assert.False(vm.IsFocused);
        vm.IsQueueCollapsed = true;
        Assert.False(vm.IsFocused);
        vm.IsStagesCollapsed = true;
        Assert.False(vm.IsFocused);
        vm.IsActivityColumnCollapsed = true;
        Assert.True(vm.IsFocused);
        vm.IsQueueCollapsed = false;
        Assert.False(vm.IsFocused);
    }

    [Fact]
    public void ActivityTabIndex_DefaultsToZero()
    {
        var vm = CreateViewModel();
        Assert.Equal(0, vm.ActivityTabIndex);
    }

    [Fact]
    public void FocusButtonLabels_ReflectFocusedState()
    {
        var vm = CreateViewModel();
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
        var vm = CreateViewModel();
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
        // Activity column (right edge): Down expanded, Right collapsed in-place.
        Assert.Equal(ChevronDirection.Down, vm.ActivityColumnChevron);
        vm.IsActivityColumnCollapsed = true;
        Assert.Equal(ChevronDirection.Right, vm.ActivityColumnChevron);
        vm.IsActivityColumnCollapsed = false;
        Assert.Equal(ChevronDirection.Down, vm.ActivityColumnChevron);
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
        Assert.Equal("Collapse Activity", vm.ActivityColumnHeaderTooltip);
        vm.IsActivityColumnCollapsed = true;
        Assert.Equal("Expand Activity", vm.ActivityColumnHeaderTooltip);
    }

    [Fact]
    public void ChevronPropertyChanged_FiresOnFlagChange()
    {
        var vm = CreateViewModel();
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
        vm.IsActivityColumnCollapsed = true;
        Assert.Contains(nameof(MainWindowViewModel.ActivityColumnChevron), received);
        Assert.Contains(nameof(MainWindowViewModel.ActivityColumnHeaderTooltip), received);
    }

    [Fact]
    public void ActivityColumnWidth_DefaultsTo340()
    {
        var vm = CreateViewModel();
        Assert.Equal(340, vm.ActivityColumnWidth);
    }

    [Fact]
    public void ActivityColumnEffectiveWidth_Returns36WhenCollapsed()
    {
        var vm = CreateViewModel();
        Assert.Equal(340, vm.ActivityColumnEffectiveWidth);
        vm.IsActivityColumnCollapsed = true;
        Assert.Equal(36, vm.ActivityColumnEffectiveWidth);
    }

    [Fact]
    public void ActivityColumnEffectiveWidth_SetWhenExpanded_UpdatesWidth()
    {
        var vm = CreateViewModel();
        vm.ActivityColumnEffectiveWidth = 500;
        Assert.Equal(500, vm.ActivityColumnWidth);
        Assert.Equal(500, vm.ActivityColumnEffectiveWidth);
    }

    [Fact]
    public void ActivityColumnEffectiveWidth_SetWhenCollapsed_DoesNotChangeStoredWidth()
    {
        var vm = CreateViewModel();
        vm.IsActivityColumnCollapsed = true;
        Assert.Equal(36, vm.ActivityColumnEffectiveWidth);

        // Setting the effective width while collapsed must not overwrite the
        // stored width that will be restored on re-expand.
        vm.ActivityColumnEffectiveWidth = 500;
        Assert.Equal(340, vm.ActivityColumnWidth);
    }

    [Fact]
    public void ActivityColumnEffectiveWidth_RestoresStoredWidthOnExpand()
    {
        var vm = CreateViewModel();
        vm.ActivityColumnWidth = 500;
        vm.IsActivityColumnCollapsed = true;
        Assert.Equal(36, vm.ActivityColumnEffectiveWidth);

        vm.IsActivityColumnCollapsed = false;
        Assert.Equal(500, vm.ActivityColumnEffectiveWidth);
        Assert.Equal(500, vm.ActivityColumnWidth);
    }

    [Fact]
    public void ActivityColumnEffectiveWidth_NotifiesOnCollapseChange()
    {
        var vm = CreateViewModel();
        var received = new List<string>();
        vm.PropertyChanged += (_, e) => received.Add(e.PropertyName!);

        received.Clear();
        vm.IsActivityColumnCollapsed = true;
        Assert.Contains(nameof(MainWindowViewModel.ActivityColumnEffectiveWidth), received);
    }
}
