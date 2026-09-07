using System.Text.Json;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;

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
}
