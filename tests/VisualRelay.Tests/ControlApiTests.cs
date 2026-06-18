using System.Text.Json;
using Avalonia.Threading;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the localhost control API core (<see cref="ControlApi"/>),
/// which backs the curl-driven HTTP control surface. These construct a real
/// <see cref="MainWindowViewModel"/> + <see cref="MainWindow"/> on the headless
/// Avalonia dispatcher (so the UI-thread marshaling inside ControlApi runs for
/// real) and assert the JSON contract + command gating WITHOUT triggering any
/// relay/swival run: gating is exercised with a command that is DISABLED in the
/// test state, and successful invocation uses the side-effect-free
/// <c>pause-toggle</c> command.
/// </summary>
[Collection("Headless")]
public sealed class ControlApiTests
{
    private static ControlApi NewApi(out MainWindowViewModel viewModel)
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm };
        viewModel = vm;
        return new ControlApi(vm, window);
    }

    [AvaloniaFact]
    public async Task BuildStateJson_IncludesCommandsMapAndStages()
    {
        var api = NewApi(out _);

        var json = await api.BuildStateJsonAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Top-level scalar fields exist.
        Assert.True(root.TryGetProperty("rootPath", out _));
        Assert.True(root.TryGetProperty("showArchive", out _));
        Assert.True(root.TryGetProperty("isBusy", out _));
        Assert.True(root.TryGetProperty("pauseRequested", out _));
        Assert.True(root.TryGetProperty("statusText", out _));

        // backend block.
        var backend = root.GetProperty("backend");
        Assert.True(backend.TryGetProperty("reachable", out _));
        Assert.True(backend.TryGetProperty("label", out _));
        Assert.True(backend.TryGetProperty("message", out _));

        // selectedTask is null when nothing selected.
        Assert.Equal(JsonValueKind.Null, root.GetProperty("selectedTask").ValueKind);

        // tasks + stages arrays.
        Assert.Equal(JsonValueKind.Array, root.GetProperty("tasks").ValueKind);
        var stages = root.GetProperty("stages");
        Assert.Equal(JsonValueKind.Array, stages.ValueKind);
        Assert.Equal(11, stages.GetArrayLength());
        var firstStage = stages[0];
        Assert.Equal(1, firstStage.GetProperty("number").GetInt32());
        Assert.False(string.IsNullOrEmpty(firstStage.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrEmpty(firstStage.GetProperty("status").GetString()));
        Assert.False(string.IsNullOrEmpty(firstStage.GetProperty("tier").GetString()));

        // commands map contains every documented command name, each with enabled flag.
        var commands = root.GetProperty("commands");
        string[] expected =
        [
            "run-all", "run-selected", "resume", "refresh", "pause-toggle",
            "archive-toggle", "new-task", "follow-running", "start-backend",
            "edit", "select-task", "bypass-sandbox", "boost-turns", "open-folder"
        ];
        foreach (var name in expected)
        {
            Assert.True(commands.TryGetProperty(name, out var entry), $"commands map missing '{name}'");
            Assert.True(entry.TryGetProperty("enabled", out var enabled), $"command '{name}' missing enabled");
            Assert.True(enabled.ValueKind is JsonValueKind.True or JsonValueKind.False);
        }
    }

    [AvaloniaFact]
    public async Task InvokeCommand_PauseToggle_FlipsPauseRequested_AndReturnsOk()
    {
        var api = NewApi(out var vm);
        var before = await Dispatcher.UIThread.InvokeAsync(() => vm.PauseRequested);

        var (status, json) = await api.InvokeCommandAsync("pause-toggle", null);

        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("pause-toggle", doc.RootElement.GetProperty("command").GetString());

        var after = await Dispatcher.UIThread.InvokeAsync(() => vm.PauseRequested);
        Assert.NotEqual(before, after);

        // /state should reflect the flipped value.
        var stateJson = await api.BuildStateJsonAsync();
        using var stateDoc = JsonDocument.Parse(stateJson);
        Assert.Equal(after, stateDoc.RootElement.GetProperty("pauseRequested").GetBoolean());
    }

    [AvaloniaFact]
    public async Task InvokeCommand_RunSelected_WithNoSelection_Returns409Disabled_AndDoesNotRun()
    {
        var api = NewApi(out var vm);
        // No task selected → RunSelectedCommand.CanExecute(null) is false.
        var busyBefore = await Dispatcher.UIThread.InvokeAsync(() => vm.IsBusy);

        var (status, json) = await api.InvokeCommandAsync("run-selected", null);

        Assert.Equal(409, status);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("disabled", doc.RootElement.GetProperty("error").GetString());

        var busyAfter = await Dispatcher.UIThread.InvokeAsync(() => vm.IsBusy);
        Assert.Equal(busyBefore, busyAfter);
        Assert.False(busyAfter);
    }

    [AvaloniaFact]
    public async Task InvokeCommand_UnknownName_Returns404()
    {
        var api = NewApi(out _);

        var (status, json) = await api.InvokeCommandAsync("nope", null);

        Assert.Equal(404, status);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown command", doc.RootElement.GetProperty("error").GetString());
    }

    [AvaloniaFact]
    public async Task InvokeCommand_OpenFolder_SetsRootPath_AndRejectsMissingFolder()
    {
        var api = NewApi(out _);
        var dir = AppContext.BaseDirectory; // a real, stable directory

        var (status, json) = await api.InvokeCommandAsync(
            "open-folder", JsonSerializer.Serialize(new { path = dir }));
        Assert.Equal(200, status);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        // /state reflects the new root.
        var stateJson = await api.BuildStateJsonAsync();
        using var stateDoc = JsonDocument.Parse(stateJson);
        Assert.Equal(dir, stateDoc.RootElement.GetProperty("rootPath").GetString());

        // A non-existent folder is refused (409) and does not change the root.
        var (missingStatus, missingJson) = await api.InvokeCommandAsync(
            "open-folder", JsonSerializer.Serialize(new { path = "/no/such/dir/vr-xyz-zzz" }));
        Assert.Equal(409, missingStatus);
        using var missingDoc = JsonDocument.Parse(missingJson);
        Assert.Equal("folder not found", missingDoc.RootElement.GetProperty("error").GetString());
    }

    [AvaloniaFact]
    public async Task BuildStateJson_WithSelectedTask_PopulatesSelectedBlock()
    {
        var api = NewApi(out var vm);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var item = new RelayTaskItem(
                "demo-task", "/tmp/demo-task.md", "/tmp", false, []);
            vm.Tasks.Add(new TaskRowViewModel(item));
            vm.SelectedTask = vm.Tasks[0];
        });

        var json = await api.BuildStateJsonAsync();
        using var doc = JsonDocument.Parse(json);
        var selected = doc.RootElement.GetProperty("selectedTask");
        Assert.Equal(JsonValueKind.Object, selected.ValueKind);
        Assert.Equal("demo-task", selected.GetProperty("id").GetString());
        Assert.True(selected.TryGetProperty("stateLabel", out _));
        Assert.True(selected.TryGetProperty("needsReview", out _));
        Assert.True(selected.TryGetProperty("metricLabel", out _));

        // tasks array now has the one row.
        var tasks = doc.RootElement.GetProperty("tasks");
        Assert.Equal(1, tasks.GetArrayLength());
        Assert.Equal("demo-task", tasks[0].GetProperty("id").GetString());
    }

    [AvaloniaFact]
    public async Task InvokeCommand_SelectTask_WithPresentAndAbsentId()
    {
        var api = NewApi(out var vm);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var item = new RelayTaskItem(
                "alpha", "/tmp/alpha.md", "/tmp", false, []);
            vm.Tasks.Add(new TaskRowViewModel(item));
        });

        var (okStatus, okJson) = await api.InvokeCommandAsync("select-task", "{\"id\":\"alpha\"}");
        Assert.Equal(200, okStatus);
        using (var okDoc = JsonDocument.Parse(okJson))
        {
            Assert.True(okDoc.RootElement.GetProperty("ok").GetBoolean());
        }
        var selectedId = await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedTask?.Id);
        Assert.Equal("alpha", selectedId);

        var (missStatus, missJson) = await api.InvokeCommandAsync("select-task", "{\"id\":\"ghost\"}");
        Assert.Equal(409, missStatus);
        using var missDoc = JsonDocument.Parse(missJson);
        Assert.False(missDoc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("task not found", missDoc.RootElement.GetProperty("error").GetString());
    }

    // NOTE on the screenshot tests: this headless harness configures the
    // production app via UseHeadless() WITHOUT a Skia/GPU rasterizer (see
    // HeadlessTestApp), so RenderTargetBitmap.Save produces no pixel data here
    // (Avalonia headless has no surface to rasterize). In the REAL desktop app
    // RenderTargetBitmap.Render(window) yields a valid PNG. We therefore assert
    // what is observable headlessly: the capture path runs end-to-end without
    // throwing, returns a non-null byte buffer, and the ?path= branch performs
    // the file write and returns the resolved absolute path. When pixel bytes
    // ARE produced (any Skia-backed context), they must carry the PNG signature.

    [AvaloniaFact]
    public async Task CaptureScreenshot_RunsAndEncodesPng_WhenBytesProduced()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 400, Height = 300 };
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            window.Show();
            window.Measure(new Avalonia.Size(400, 300));
            window.Arrange(new Avalonia.Rect(0, 0, 400, 300));
        });
        var api = new ControlApi(vm, window);

        var (png, writtenPath) = await api.CaptureScreenshotAsync(null);

        Assert.Null(writtenPath);
        Assert.NotNull(png);
        // If a rasterizer produced pixels, the buffer must be a real PNG.
        if (png.Length > 0)
        {
            Assert.True(png.Length > 8);
            Assert.Equal(0x89, png[0]);
            Assert.Equal((byte)'P', png[1]);
            Assert.Equal((byte)'N', png[2]);
            Assert.Equal((byte)'G', png[3]);
        }
    }

    [AvaloniaFact]
    public async Task CaptureScreenshot_WithPath_WritesFileAndReturnsResolvedPath()
    {
        var vm = new MainWindowViewModel();
        var window = new MainWindow { DataContext = vm, Width = 400, Height = 300 };
        var api = new ControlApi(vm, window);

        var dir = Path.Combine(Path.GetTempPath(), "vr-control-shot", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(dir, "shot.png");
        try
        {
            var (_, writtenPath) = await api.CaptureScreenshotAsync(target);

            Assert.Equal(Path.GetFullPath(target), writtenPath);
            Assert.True(File.Exists(target));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
