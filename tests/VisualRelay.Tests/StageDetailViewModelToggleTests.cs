using System.ComponentModel;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed class StageDetailViewModelToggleTests
{
    // ── Default toggle state ────────────────────────────────────────────

    [Fact]
    public void IsInputRawText_DefaultsToFalse()
    {
        var vm = new StageDetailViewModel();
        Assert.False(vm.IsInputRawText);
    }

    [Fact]
    public void IsOutputRawJson_DefaultsToFalse()
    {
        var vm = new StageDetailViewModel();
        Assert.False(vm.IsOutputRawJson);
    }

    // ── Computed visibility helpers ─────────────────────────────────────

    [Fact]
    public void IsInputReadyAndNotRawText_TrueWhenReadyAndNotRaw()
    {
        // Simulate Load setting state (without filesystem dependency).
        var vm = new StageDetailViewModel();
        vm.IsInputRawText = false;
        vm.InputState = StageDetailState.Ready;

        Assert.True(vm.IsInputReadyAndNotRawText);
        Assert.False(vm.IsInputReadyAndRawText);
    }

    [Fact]
    public void IsInputReadyAndRawText_TrueWhenReadyAndRaw()
    {
        var vm = new StageDetailViewModel();
        vm.IsInputRawText = true;
        vm.InputState = StageDetailState.Ready;

        Assert.True(vm.IsInputReadyAndRawText);
        Assert.False(vm.IsInputReadyAndNotRawText);
    }

    [Fact]
    public void IsOutputReadyAndNotRawJson_TrueWhenReadyAndNotRaw()
    {
        var vm = new StageDetailViewModel();
        vm.IsOutputRawJson = false;
        vm.OutputState = StageDetailState.Ready;

        Assert.True(vm.IsOutputReadyAndNotRawJson);
        Assert.False(vm.IsOutputReadyAndRawJson);
    }

    [Fact]
    public void IsOutputReadyAndRawJson_TrueWhenReadyAndRaw()
    {
        var vm = new StageDetailViewModel();
        vm.IsOutputRawJson = true;
        vm.OutputState = StageDetailState.Ready;

        Assert.True(vm.IsOutputReadyAndRawJson);
        Assert.False(vm.IsOutputReadyAndNotRawJson);
    }

    // ── PropertyChanged notifications for raw-visibility helpers ─────────

    /// <summary>
    /// When InputState transitions to Ready the computed properties
    /// IsInputReadyAndNotRawText / IsInputReadyAndRawText must fire
    /// PropertyChanged so Avalonia bindings re-evaluate.  Without this the
    /// parsed-input view stays hidden until the user manually toggles the
    /// Raw checkbox (the original bug).
    /// </summary>
    [Fact]
    public void InputStateChangedToReady_NotifiesRawVisibilityHelpers()
    {
        var vm = new StageDetailViewModel();
        var changed = new HashSet<string?>();

        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        // Set IsInputRawText first (it stays false) then transition state.
        vm.IsInputRawText = false;
        changed.Clear();

        vm.InputState = StageDetailState.Ready;

        Assert.Contains("IsInputReadyAndNotRawText", changed);
        Assert.Contains("IsInputReadyAndRawText", changed);
    }

    /// <summary>
    /// Same invariant as InputStateChangedToReady_NotifiesRawVisibilityHelpers
    /// but for the Output tab's Raw JSON toggle.
    /// </summary>
    [Fact]
    public void OutputStateChangedToReady_NotifiesRawVisibilityHelpers()
    {
        var vm = new StageDetailViewModel();
        var changed = new HashSet<string?>();

        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.IsOutputRawJson = false;
        changed.Clear();

        vm.OutputState = StageDetailState.Ready;

        Assert.Contains("IsOutputReadyAndNotRawJson", changed);
        Assert.Contains("IsOutputReadyAndRawJson", changed);
    }

    // ── Edge cases ──────────────────────────────────────────────────────

    [Fact]
    public void NoStage_AllComputedVisibilityHelpersAreFalse()
    {
        var vm = new StageDetailViewModel(); // default is NoStage

        Assert.False(vm.IsInputReadyAndNotRawText);
        Assert.False(vm.IsInputReadyAndRawText);
        Assert.False(vm.IsOutputReadyAndNotRawJson);
        Assert.False(vm.IsOutputReadyAndRawJson);
    }

    [Fact]
    public void InputNotStarted_RawVisibilityHelpersAreFalse()
    {
        var vm = new StageDetailViewModel();
        vm.InputState = StageDetailState.NotStarted;

        Assert.False(vm.IsInputReadyAndNotRawText);
        Assert.False(vm.IsInputReadyAndRawText);
    }

    [Fact]
    public void OutputNotComplete_RawVisibilityHelpersAreFalse()
    {
        var vm = new StageDetailViewModel();
        vm.OutputState = StageDetailState.NotComplete;

        Assert.False(vm.IsOutputReadyAndNotRawJson);
        Assert.False(vm.IsOutputReadyAndRawJson);
    }
}
