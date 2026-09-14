using System.Text.Json;
using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// A refused run command says what blocks it. Measured on the Windows arm: a pause armed
/// during one repository's drain outlived its cancel and the next open-folder, and the
/// next repository's <c>run-all</c> came back as a bare 409 "disabled" with a task pending.
/// Nothing in the reply pointed at the pause, so the caller had to reverse-engineer /state.
/// </summary>
public sealed partial class ControlApiTests
{
    [AvaloniaFact]
    public async Task InvokeCommand_RunAll_WhilePaused_NamesThePauseAndWhatLiftsIt()
    {
        var api = NewApi(out var vm);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.Tasks.Add(new TaskRowViewModel(new RelayTaskItem("alpha", "/tmp/alpha.md", "/tmp", false, [])));
            vm.TogglePauseCommand.Execute(null);
        });

        var (status, json) = await api.InvokeCommandAsync("run-all", null);

        Assert.Equal(409, status);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("disabled", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal("paused: pause-toggle resumes", doc.RootElement.GetProperty("reason").GetString());
    }

    [AvaloniaFact]
    public async Task InvokeCommand_RunSelected_WithNothingSelectedAndPaused_NamesBothBlockers()
    {
        var api = NewApi(out var vm);
        await Dispatcher.UIThread.InvokeAsync(() => vm.TogglePauseCommand.Execute(null));

        var (status, json) = await api.InvokeCommandAsync("run-selected", null);

        Assert.Equal(409, status);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(
            "paused: pause-toggle resumes; no task is selected: select-task picks one",
            doc.RootElement.GetProperty("reason").GetString());
    }

    [AvaloniaFact]
    public async Task InvokeCommand_RunAll_OnAnEmptyQueue_SaysTheQueueIsEmpty()
    {
        var api = NewApi(out _);

        var (_, json) = await api.InvokeCommandAsync("run-all", null);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("the queue has no tasks: create-task adds one", doc.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>
    /// Reset only returns a flagged task to Pending. Measured on the Windows arm: reset-selected on a
    /// Pending task whose planning stages had finished came back as a bare 409 "disabled".
    /// </summary>
    [AvaloniaFact]
    public async Task InvokeCommand_ResetSelected_OnATaskThatIsNotFlagged_SaysOnlyAFlaggedTaskResets()
    {
        var api = NewApi(out var vm);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.Tasks.Add(new TaskRowViewModel(new RelayTaskItem("alpha", "/tmp/alpha.md", "/tmp", false, [])));
            vm.SelectedTask = vm.Tasks[0];
        });

        var (status, json) = await api.InvokeCommandAsync("reset-selected", "{\"confirm\":true}");

        Assert.Equal(409, status);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("the selected task is not flagged: reset returns a flagged task to Pending",
            doc.RootElement.GetProperty("reason").GetString());
    }

    [AvaloniaFact]
    public async Task InvokeCommand_ResetSelected_WithNothingSelected_SaysSo()
    {
        var api = NewApi(out _);

        var (_, json) = await api.InvokeCommandAsync("reset-selected", "{\"confirm\":true}");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("no task is selected: select-task picks one", doc.RootElement.GetProperty("reason").GetString());
    }
}
