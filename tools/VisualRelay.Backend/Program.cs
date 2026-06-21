using VisualRelay.Core.Execution;

// VisualRelay.Backend — lifecycle manager for the local model backend (a LiteLLM
// proxy) on http://127.0.0.1:4000. The published, self-contained successor to the
// retired tools/backend/backend.sh: subcommands start | stop | status, dispatched
// to the shared C# BackendLifecycle in VisualRelay.Core (the SAME code the GUI
// autostart path runs). Numeric exit codes mirror the old script — 0 success/
// healthy, 1 failure/down, 2 usage.

var cmd = args.Length > 0 ? args[0] : string.Empty;

var lifecycle = new BackendLifecycle();

BackendResult result;
switch (cmd)
{
    case "start":
        result = await lifecycle.StartAsync();
        break;
    case "stop":
        result = await lifecycle.StopAsync();
        break;
    case "status":
        result = await lifecycle.StatusAsync();
        break;
    default:
        Console.Error.WriteLine("usage: VisualRelay.Backend {start|stop|status}");
        return 2;
}

if (result.Status is { } line)
    Console.WriteLine(line);
return result.ExitCode;
