using System.Net;

namespace VisualRelay.App.Services;

/// <summary>
/// Embedded localhost HTTP control server. Binds a <see cref="HttpListener"/> to
/// <c>http://127.0.0.1:&lt;port&gt;/</c> (loopback ONLY — never <c>+</c>/<c>*</c>,
/// so macOS shows no firewall prompt and the surface is not remotely reachable)
/// and dispatches to <see cref="ControlApi"/>. The accept loop runs on a
/// background thread; ControlApi marshals every VM/window touch onto the UI
/// thread. A startup failure (e.g. port in use) is caught and logged — it never
/// crashes or blocks app startup.
/// </summary>
public sealed partial class ControlServer(ControlApi api, ControlServerOptions options) : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    /// <summary>The loopback prefix the listener binds (also its confirmation URL).</summary>
    private string Url => $"http://127.0.0.1:{options.Port}/";

    /// <summary>
    /// Starts the listener if enabled. Never throws: a bind/start failure is
    /// caught and reported to stderr/Console so app startup always continues.
    /// On success, writes one confirmation line to Console.
    /// </summary>
    public void Start()
    {
        if (!options.Enabled)
        {
            Console.Error.WriteLine("vr-control: disabled via VR_CONTROL_DISABLE");
            return;
        }

        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(Url);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            Console.Error.WriteLine($"vr-control: listening on http://127.0.0.1:{options.Port}");
        }
        catch (Exception ex)
        {
            // Port in use, HttpListener access error, etc. Degrade gracefully.
            Console.Error.WriteLine($"vr-control: failed to start ({ex.Message}); control API disabled");
            _listener = null;
        }
    }

    /// <summary>Releases the listener (idempotent; delegates to Stop()).</summary>
    public void Dispose() => Stop();

    /// <summary>Stops the listener and the accept loop. Safe to call when not started.</summary>
    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // Best-effort shutdown — never throw from teardown.
        }
        finally
        {
            _listener = null;
            _cts = null;
        }

        // Await the accept loop to completion so the socket is fully torn down
        // before Stop() returns. Bounded timeout preserves the "never hang"
        // contract. Any exception from the accept loop is swallowed — best-effort
        // teardown must never throw.
        if (_acceptLoop is not null)
        {
            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Accept loop faulted or timed out — ignore.
            }
            finally
            {
                _acceptLoop = null;
            }
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception)
            {
                // Listener stopped/disposed during shutdown, or a transient
                // accept error: exit the loop on cancellation, else keep serving.
                if (token.IsCancellationRequested || !listener.IsListening)
                {
                    return;
                }

                continue;
            }

            // Handle each request without blocking the accept loop.
            _ = Task.Run(() => HandleContextSafeAsync(context), token);
        }
    }

    private async Task HandleContextSafeAsync(HttpListenerContext context)
    {
        try
        {
            await RouteAsync(context);
        }
        catch (Exception ex)
        {
            await TryWriteErrorAsync(context, ex);
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch
            {
                // Response already closed/aborted — ignore.
            }
        }
    }

    private static async Task TryWriteErrorAsync(HttpListenerContext context, Exception ex)
    {
        try
        {
            var json = Json.Object(("ok", false), ("error", "internal error"), ("detail", ex.Message));
            context.Response.StatusCode = 500;
            await WriteJsonAsync(context, json);
        }
        catch
        {
            // Nothing more we can do.
        }
    }
}
