using System.Diagnostics;
using VisualRelay.App.ViewModels;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

public sealed class StartBackendAsyncTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "vr-vm-startbackend", Guid.NewGuid().ToString("N"));

    public StartBackendAsyncTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task StartBackendAsync_RunsSharedLifecycleAndIsTraceable()
    {
        // The one-click "Start backend" command now runs the SHARED C#
        // BackendLifecycle (replacing the tools/backend/backend.sh shell-out) and
        // routes its diagnostics to Trace so a degraded/failed start is observable
        // rather than swallowed by an empty catch (the InspectCode
        // EmptyGeneralCatchClause guarantee). It must never throw on the UI path.
        var viewModel = new MainWindowViewModel();

        // Inject a hermetic lifecycle: unhealthy + no toolchain => it logs the
        // remediation and returns Down WITHOUT provisioning a venv or spawning a
        // real proxy. State is isolated under a temp XDG home.
        var paths = BackendPaths.Resolve(new DictionaryEnvironmentAccessor { ["HOME"] = _home });
        var traced = new List<string>();
        viewModel.BackendLifecycleFactory = () => new BackendLifecycle(
            paths,
            new BackendStartOptions { ReadyTimeout = TimeSpan.FromMilliseconds(50) },
            log: line => { traced.Add(line); Trace.WriteLine($"backend: {line}"); },
            healthCheck: _ => Task.FromResult(false),
            ensureVenv: (_, _) => new BackendVenv.Result(null));

        var writer = new StringWriter();
        var listener = new TextWriterTraceListener(writer);
        Trace.Listeners.Add(listener);
        try
        {
            // Drive the command via its generated IAsyncRelayCommand (Rename-safe).
            await viewModel.StartBackendCommand.ExecuteAsync(null);

            listener.Flush();
            Assert.Contains("backend:", writer.ToString());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }

        // The missing-toolchain remediation was emitted (observable degrade).
        Assert.Contains(traced, l => l.Contains("could not start the model backend"));
    }
}
