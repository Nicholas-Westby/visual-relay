using System.Text.Json;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// An operator driving the app over the control API sees bootstrap's outcome only
/// through <c>/state.statusText</c>, so the note about the uncommitted config has to
/// survive the refresh the command runs on its way out.
/// </summary>
[Collection("Headless")]
public sealed class ControlApiBootstrapStatusTests
{
    [AvaloniaFact]
    public async Task Bootstrap_StateStatusText_CarriesTheUncommittedConfigNote()
    {
        using var repo = TestRepository.Create();
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        var api = new ControlApi(vm);
        await vm.LoadInitialAsync();

        var (status, _) = await api.InvokeCommandAsync("bootstrap", null);
        Assert.Equal(200, status);
        if (vm.BootstrapProjectCommand.ExecutionTask is { } execution)
            await execution;

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());
        Assert.Contains(
            "Config written to .relay/config.json and left uncommitted.",
            doc.RootElement.GetProperty("statusText").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A script polls /state to learn when the app is ready for the next command.
    /// While bootstrap runs, isBusy says so and the commands that refuse a busy app
    /// refuse it, so a drain cannot start on a half-written config.
    /// </summary>
    [AvaloniaFact]
    public async Task Bootstrap_WhileItRuns_StateIsBusy_AndTheRunCommandsAreRefused()
    {
        using var repo = TestRepository.Create();
        // RunContinuationsAsynchronously: the gate is released from the dispatcher
        // thread, and an inline resume would re-enter it from under itself.
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new MainWindowViewModel(repo.Env)
        {
            RootPath = repo.Root,
            BootstrapRunner = async _ =>
            {
                await held.Task;
                return new ProjectBootstrapResult(
                    GitInitialized: false, HookInstalled: true, HookWarning: null,
                    UsedPlaceholderTestCommand: false, TestCommand: "npm test",
                    ConfigPath: ".relay/config.json");
            },
        };
        var api = new ControlApi(vm);
        await vm.LoadInitialAsync();

        var (status, _) = await api.InvokeCommandAsync("bootstrap", null);
        Assert.Equal(200, status);

        using (var doc = JsonDocument.Parse(await api.BuildStateJsonAsync()))
        {
            Assert.True(doc.RootElement.GetProperty("isBusy").GetBoolean());
            Assert.Equal(
                "Bootstrapping: checking test commands",
                doc.RootElement.GetProperty("statusText").GetString());
        }

        var (runAll, runAllJson) = await api.InvokeCommandAsync("run-all", null);
        Assert.Equal(409, runAll);
        using (var doc = JsonDocument.Parse(runAllJson))
        {
            Assert.Contains(
                "run",
                doc.RootElement.GetProperty("reason").GetString()!,
                StringComparison.OrdinalIgnoreCase);
        }

        var (create, _) = await api.InvokeCommandAsync(
            "create-task", "{\"title\":\"Mid bootstrap\"}");
        Assert.Equal(409, create);

        held.SetResult();
        if (vm.BootstrapProjectCommand.ExecutionTask is { } execution)
            await execution;

        using var after = JsonDocument.Parse(await api.BuildStateJsonAsync());
        Assert.False(after.RootElement.GetProperty("isBusy").GetBoolean());
    }
}
