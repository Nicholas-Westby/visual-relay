using System.Text.Json;
using Avalonia.Threading;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// <c>POST /command/cancel</c> is the only way to stop a running drain short of
/// killing the process. It is available exactly while a run is in flight, cancels
/// that run's token, and reports itself through <c>/state</c> so a caller can watch
/// the run wind down.
/// </summary>
[Collection("Headless")]
public sealed class ControlApiCancelTests
{
    // No window: none of these assertions touch the screenshot surface, and the
    // whole-app boot guard reserves `new MainWindow` for tests that need it.
    private static ControlApi NewApi(out MainWindowViewModel viewModel)
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        viewModel = vm;
        return new ControlApi(vm);
    }

    [AvaloniaFact]
    public async Task InvokeCommand_Cancel_WhileIdle_Returns409Disabled()
    {
        var api = NewApi(out _);

        var (status, json) = await api.InvokeCommandAsync("cancel", null);

        Assert.Equal(409, status);
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("disabled", doc.RootElement.GetProperty("error").GetString());
    }

    [AvaloniaFact]
    public async Task InvokeCommand_Cancel_WhileARunIsBusy_CancelsThatRunsToken()
    {
        var api = NewApi(out var vm);
        CancellationToken runToken = default;
        var run = vm.RunCancellableForTestsAsync(async token =>
        {
            runToken = token;
            try { await new TaskCompletionSource().Task.WaitAsync(token); }
            catch (OperationCanceledException) { /* the run winds itself down */ }
        });

        var (status, _) = await api.InvokeCommandAsync("cancel", null);

        Assert.Equal(200, status);
        Assert.True(runToken.IsCancellationRequested);
        await run;
    }

    [AvaloniaFact]
    public async Task State_CancelRequested_IsTrueUntilTheRunHasWoundDown()
    {
        var api = NewApi(out var vm);
        Assert.False(await ReadCancelRequested(api));
        Assert.False(await ReadCancelEnabled(api));

        var release = new TaskCompletionSource();
        var run = vm.RunCancellableForTestsAsync(_ => release.Task);
        Assert.True(await ReadCancelEnabled(api));

        await api.InvokeCommandAsync("cancel", null);
        Assert.True(await ReadCancelRequested(api));

        release.SetResult();
        await run;

        Assert.False(await ReadCancelRequested(api));
        Assert.False(await ReadCancelEnabled(api));
        var statusText = await Dispatcher.UIThread.InvokeAsync(() => vm.StatusText);
        Assert.Contains("Cancelled", statusText, StringComparison.Ordinal);
    }

    private static async Task<bool> ReadCancelRequested(ControlApi api)
    {
        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        return doc.RootElement.GetProperty("cancelRequested").GetBoolean();
    }

    private static async Task<bool> ReadCancelEnabled(ControlApi api)
    {
        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        return doc.RootElement.GetProperty("commands").GetProperty("cancel").GetProperty("enabled").GetBoolean();
    }
}
