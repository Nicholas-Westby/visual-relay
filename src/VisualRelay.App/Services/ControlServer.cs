using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace VisualRelay.App.Services;

/// <summary>
/// Embedded localhost HTTP control server. Hosts the control API handler on
/// Kestrel (ASP.NET Core) bound to <c>http://127.0.0.1:&lt;port&gt;/</c>
/// (loopback ONLY — never <c>+</c>/<c>*</c>, so macOS shows no firewall
/// prompt and the surface is not remotely reachable). Kestrel manages its
/// own accept loop internally; ControlApi marshals every VM/window touch
/// onto the UI thread. A startup failure (e.g. port in use) is caught and
/// logged — unless <see cref="ControlServerOptions.PortWasExplicitlySet"/>
/// is true, in which case the exception is re-thrown so the caller can fail
/// fast.
/// </summary>
public sealed partial class ControlServer(ControlApi api, ControlServerOptions options) : IDisposable
{
    private WebApplication? _app;
    private int _boundPort;

    /// <summary>The actual port the server bound (useful when <see cref="ControlServerOptions.Port"/> is 0).</summary>
    public int BoundPort => _boundPort;

    /// <summary>
    /// True when the control server successfully started and is listening.
    /// False when disabled, not yet started, or the bind/start failed.
    /// </summary>
    public bool IsAvailable => _app is not null;

    /// <summary>
    /// Starts the server if enabled. When <see cref="ControlServerOptions.PortWasExplicitlySet"/>
    /// is true, a bind/start failure throws rather than silently disabling the
    /// control API. On success, writes one confirmation line to Console.
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
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
            builder.Logging.ClearProviders();
            builder.WebHost.UseKestrel();
            builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");

            var app = builder.Build();
            app.Run(BuildHandler(api, options));

            // Run StartAsync on the thread pool so Kestrel never synchronises
            // back to the Avalonia UI dispatcher, which would deadlock when
            // the calling thread blocks on GetAwaiter().GetResult().
            Task.Run(() => app.StartAsync()).GetAwaiter().GetResult();

            // Read the actual bound port (surfaces port 0 → OS-assigned port).
            _boundPort = app.Urls
                .Select(u => new Uri(u))
                .Where(u => u.Port > 0)
                .Select(u => u.Port)
                .FirstOrDefault();
            if (_boundPort == 0) _boundPort = options.Port;

            _app = app;
            options.ControlPort = _boundPort;

            Console.Error.WriteLine($"vr-control: listening on http://127.0.0.1:{_boundPort}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"vr-control: failed to start ({ex.Message}); control API disabled");
            _app = null;
            if (options.PortWasExplicitlySet)
            {
                throw new InvalidOperationException(
                    $"Control API could not bind port {options.Port}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Releases the server (idempotent; delegates to Stop()).</summary>
    public void Dispose() => Stop();

    /// <summary>
    /// Stops the server. Safe to call when not started. Bounded at 5 s by
    /// a CancellationToken; any exception is swallowed — best-effort
    /// teardown must never throw. The stop runs on the thread pool to avoid
    /// synchronising back to the Avalonia dispatcher and deadlocking.
    /// </summary>
    public void Stop()
    {
        var app = _app;
        _app = null;

        if (app is null) return;

        // Offload the async stop to the thread pool so nothing synchronises
        // back to the Avalonia dispatcher, which would deadlock. The 5 s
        // CancellationToken passed to app.StopAsync is what bounds teardown
        // (IHost.StopAsync honors it and forces shutdown at 5 s).
        Task.Run(async () =>
        {
            try
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await app.StopAsync(stopCts.Token);
            }
            catch
            {
                // Best-effort shutdown.
            }
            finally
            {
                try { (app as IDisposable).Dispose(); }
                catch { /* Best-effort cleanup; nothing to do. */ }
            }
        }).GetAwaiter().GetResult();
    }
}
