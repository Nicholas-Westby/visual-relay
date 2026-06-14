using System.Diagnostics;
using System.Reflection;
using VisualRelay.App.ViewModels;

namespace VisualRelay.Tests;

public sealed class StartBackendAsyncTests
{
    [Fact]
    public async Task StartBackendAsync_LogsWhenToolchainIsMissing()
    {
        // StartBackendAsync has an empty catch { } that swallows
        // Process.Start failures silently — InspectCode flags this as
        // EmptyGeneralCatchClause. The fix adds Debug.WriteLine so the
        // catch is non-empty and the failure is traceable.
        var viewModel = new MainWindowViewModel();

        // Capture Debug/Trace output in a StringWriter.
        var writer = new StringWriter();
        var listener = new TextWriterTraceListener(writer);
        Trace.Listeners.Add(listener);

        try
        {
            var method = typeof(MainWindowViewModel).GetMethod(
                "StartBackendAsync",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method); // fail early if Rename breaks reflection

            var task = (Task)method.Invoke(viewModel, null)!;
            await task;

            // After the fix, the catch block must log a diagnostic
            // message so the swallowed exception is at least traceable.
            listener.Flush();
            var output = writer.ToString();
            Assert.NotEmpty(output);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
        }
    }
}
