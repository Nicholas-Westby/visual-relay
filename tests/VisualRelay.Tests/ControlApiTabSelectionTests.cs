using System.Text.Json;
using Avalonia.Threading;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.App.Views;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the tab-navigation control commands (<c>select-activity-tab</c> /
/// <c>select-detail-tab</c>). They let a caller drive the live window to a
/// specific tab BY IDENTITY (index or header name) before <c>GET /screenshot</c>,
/// so a non-default tab's content is screenshot-verifiable. Each must set the
/// bound tab index on the UI thread and appear (enabled) in the <c>/state</c>
/// commands map.
/// </summary>
[Collection("Headless")]
public sealed class ControlApiTabSelectionTests
{
    private static ControlApi NewApi(out MainWindowViewModel viewModel)
    {
        // Route ui-state persistence to a throwaway dir so setting the activity
        // tab never writes the developer/CI real ~/.config/visual-relay.
        var dir = Path.Combine(Path.GetTempPath(), "vr-control-tabs", Guid.NewGuid().ToString("N"));
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = dir });
        var window = new MainWindow { DataContext = vm };
        viewModel = vm;
        return new ControlApi(vm, window);
    }

    [AvaloniaFact]
    public async Task SelectActivityTab_ByIndex_SetsActivityTabIndex()
    {
        var api = NewApi(out var vm);

        var (status, json) = await api.InvokeCommandAsync("select-activity-tab", "{\"index\":4}");

        Assert.Equal(200, status);
        using (var doc = JsonDocument.Parse(json))
        {
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        }

        var index = await Dispatcher.UIThread.InvokeAsync(() => vm.ActivityTabIndex);
        Assert.Equal(4, index);
    }

    [AvaloniaFact]
    public async Task SelectActivityTab_ByName_SetsActivityTabIndex()
    {
        var api = NewApi(out var vm);

        var (status, _) = await api.InvokeCommandAsync("select-activity-tab", "{\"name\":\"Output\"}");

        Assert.Equal(200, status);
        var index = await Dispatcher.UIThread.InvokeAsync(() => vm.ActivityTabIndex);
        Assert.Equal(4, index);
    }

    [AvaloniaFact]
    public async Task SelectDetailTab_ByIndexAndName_SetsSelectedTabIndex()
    {
        var api = NewApi(out var vm);

        var (s1, _) = await api.InvokeCommandAsync("select-detail-tab", "{\"index\":2}");
        Assert.Equal(200, s1);
        Assert.Equal(2, await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedTabIndex));

        var (s2, _) = await api.InvokeCommandAsync("select-detail-tab", "{\"name\":\"Context\"}");
        Assert.Equal(200, s2);
        Assert.Equal(1, await Dispatcher.UIThread.InvokeAsync(() => vm.SelectedTabIndex));
    }

    [AvaloniaFact]
    public async Task SelectTab_OutOfRangeIndexOrUnknownName_IsRefused_WithDistinctMessages()
    {
        var api = NewApi(out _);

        // An out-of-range INDEX and an unknown NAME are different mistakes; the 409
        // message must distinguish them so a caller knows which to fix.
        var (oob, oobJson) = await api.InvokeCommandAsync("select-activity-tab", "{\"index\":99}");
        Assert.Equal(409, oob);
        using (var oobDoc = JsonDocument.Parse(oobJson))
        {
            Assert.Equal("tab index 99 out of range", oobDoc.RootElement.GetProperty("error").GetString());
        }

        var (bad, badJson) = await api.InvokeCommandAsync("select-detail-tab", "{\"name\":\"Nope\"}");
        Assert.Equal(409, bad);
        using var doc = JsonDocument.Parse(badJson);
        Assert.Equal("unknown tab name 'Nope'", doc.RootElement.GetProperty("error").GetString());
    }

    [AvaloniaFact]
    public async Task TabSelectCommands_AppearEnabled_InStateCommandsMap()
    {
        var api = NewApi(out _);

        var json = await api.BuildStateJsonAsync();
        using var doc = JsonDocument.Parse(json);
        var commands = doc.RootElement.GetProperty("commands");

        foreach (var name in new[] { "select-activity-tab", "select-detail-tab" })
        {
            Assert.True(commands.TryGetProperty(name, out var entry), $"commands map missing '{name}'");
            Assert.True(entry.GetProperty("enabled").GetBoolean(), $"'{name}' should be enabled");
        }
    }
}
