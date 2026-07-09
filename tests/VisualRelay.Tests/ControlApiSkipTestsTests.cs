using System.Text.Json;
using Avalonia.Threading;
using VisualRelay.App.ViewModels;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed partial class ControlApiTests
{
    [AvaloniaFact]
    public async Task InvokeCommand_SkipTests_WithSelectedTask_TogglesAndPersists()
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig("dotnet test", []);
        repo.WriteTask("docs-task", "# Docs\n");
        var api = NewApi(out var vm);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            vm.RootPath = repo.Root;
            var item = new RelayTaskItem(
                "docs-task", Path.Combine(repo.Root, "tasks", "docs-task.md"), repo.Root, false, []);
            vm.Tasks.Clear();
            vm.Tasks.Add(new TaskRowViewModel(item));
            vm.SelectedTask = vm.Tasks[0];
        });

        // Toggle on.
        var (okStatus, _) = await api.InvokeCommandAsync("skip-tests",
            JsonSerializer.Serialize(new { value = true }));
        Assert.Equal(200, okStatus);

        var isChecked = await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedTaskSkipsTests);
        Assert.True(isChecked);

        // Toggle off.
        var (okStatus2, _) = await api.InvokeCommandAsync("skip-tests",
            JsonSerializer.Serialize(new { value = false }));
        Assert.Equal(200, okStatus2);

        var isUnchecked = await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedTaskSkipsTests);
        Assert.False(isUnchecked);
    }
}
