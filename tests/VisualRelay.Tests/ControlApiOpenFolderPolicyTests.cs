using System.Text.Json;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// open-folder answers a folder the way Browse does. Confirmed on the Windows arm: the API opened
/// C:\dev\vr-drive-probe with HTTP 200, a root Browse refuses and every launch then refused, because
/// the command skipped the folder-pick policy that keeps Windows workspaces inside a WSL distro.
/// </summary>
public sealed partial class ControlApiTests
{
    [AvaloniaTheory]
    [InlineData(@"C:\dev\vr-drive-probe", "DrvFs")]
    [InlineData(@"\\fileserver\projects\repo", "network share")]
    public async Task OpenFolder_OnWindows_RefusesAFolderBrowseWouldRefuse_AndSaysWhy(string path, string reasonPart)
    {
        var vm = new MainWindowViewModel(new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var rootBefore = vm.RootPath;
        var api = new ControlApi(vm, isWindows: true);

        var (status, json) = await api.InvokeCommandAsync("open-folder", JsonSerializer.Serialize(new { path }));

        Assert.Equal(409, status);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("folder refused", doc.RootElement.GetProperty("error").GetString());
        Assert.Contains(reasonPart, doc.RootElement.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Equal(rootBefore, vm.RootPath);
    }
}
