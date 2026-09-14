using System.Text.Json;
using Avalonia.Threading;

namespace VisualRelay.Tests;

/// <summary>
/// /state says why a config was refused. Measured on the Windows arm: one rejected
/// sandboxExtraAllowPaths entry made the config Malformed, the task list came back empty and
/// statusText read "0 pending", and nothing in /state named the entry, so the tasks looked gone.
/// </summary>
public sealed partial class ControlApiTests
{
    [AvaloniaFact]
    public async Task BuildStateJson_ACalledOutConfig_CarriesItsDiagnostic()
    {
        var api = NewApi(out var vm);
        const string diagnostic =
            "relay config: sandboxExtraAllowPaths entry must resolve under $HOME or workspace root, got: \"/home/u/.nuget/NuGet\"";
        await Dispatcher.UIThread.InvokeAsync(() => vm.ConfigDiagnostic = diagnostic);

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());

        Assert.Equal(diagnostic, doc.RootElement.GetProperty("configDiagnostic").GetString());
    }

    [AvaloniaFact]
    public async Task BuildStateJson_AHealthyConfig_HasANullDiagnostic()
    {
        var api = NewApi(out _);

        using var doc = JsonDocument.Parse(await api.BuildStateJsonAsync());

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("configDiagnostic").ValueKind);
    }
}
