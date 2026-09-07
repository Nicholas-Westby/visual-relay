using System.Text.Json;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Init;

namespace VisualRelay.Tests;

/// <summary>
/// <c>GET /state</c> answers "is this repo's gate vacuous?" so an operator driving the
/// app over the control API never mistakes a placeholder's green for a passing suite.
/// </summary>
[Collection("Headless")]
public sealed class ControlApiPlaceholderStateTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildStateJson_ReportsWhetherTheGateIsAPlaceholder(bool placeholder)
    {
        using var repo = TestRepository.Create();
        repo.WriteConfig(
            placeholder ? ProjectBootstrapper.PlaceholderTestCommand : "dotnet test", []);
        repo.WriteTask("alpha", "# Alpha\n");
        var vm = new MainWindowViewModel(repo.Env) { RootPath = repo.Root };
        var api = new ControlApi(vm);
        await vm.LoadInitialAsync();

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());

        Assert.Equal(placeholder, doc.RootElement.GetProperty("testCommandIsPlaceholder").GetBoolean());
    }
}
