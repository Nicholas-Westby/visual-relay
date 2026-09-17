using Microsoft.AspNetCore.Http;
using VisualRelay.App.Services;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

/// <summary>
/// /state and every command run on the UI thread, so a request that takes seconds
/// means something is holding that thread. Twice on the Windows arm /state took over
/// twenty seconds and nothing recorded which request it was; the handler now says so
/// on stderr, which is the evidence a follow-up would start from.
/// </summary>
public sealed class ControlServerSlowRequestLogTests
{
    private static async Task<List<string>> InvokeAsync(string method, string path, params int[] millisecondSteps)
    {
        var vm = new MainWindowViewModel(
            new DictionaryEnvironmentAccessor { ["XDG_CONFIG_HOME"] = Path.GetTempPath() });
        var options = new ControlServerOptions(Enabled: true, Port: 0, Token: null,
            InstanceId: "slow-" + Guid.NewGuid().ToString("N"));

        // The handler reads the clock once before routing and once after; the steps are
        // the gaps between successive reads, so no test has to actually be slow.
        var at = new DateTimeOffset(2026, 9, 14, 7, 40, 16, TimeSpan.Zero);
        var reads = 0;
        var log = new List<string>();
        var handler = ControlServer.BuildHandler(
            new ControlApi(vm),
            options,
            now: () =>
            {
                var current = at;
                if (reads < millisecondSteps.Length) at = at.AddMilliseconds(millisecondSteps[reads]);
                reads++;
                return current;
            },
            log: log.Add);

        var context = new DefaultHttpContext
        {
            Request = { Method = method, Path = path },
            Response = { Body = new MemoryStream() }
        };
        await handler(context);
        return log;
    }

    [Fact]
    public async Task ARequestOverTheThreshold_LogsOneLineNamingTheMethodAndPath()
    {
        var log = await InvokeAsync("GET", "/state", 3000);

        Assert.Equal(["vr-control: slow request GET /state took 3000 ms"], log);
    }

    [Fact]
    public async Task AFastRequest_LogsNothing()
    {
        Assert.Empty(await InvokeAsync("GET", "/state", 100));
    }

    [Fact]
    public async Task ASlowScreenshot_LogsNothing()
    {
        // It renders the window; slow is what it is, not a symptom.
        Assert.Empty(await InvokeAsync("GET", "/screenshot", 9000));
    }

    [Fact]
    public async Task AnUnknownRouteThatIsSlow_IsStillLogged()
    {
        var log = await InvokeAsync("POST", "/command/nope", 2500);

        Assert.Equal(["vr-control: slow request POST /command/nope took 2500 ms"], log);
    }
}
