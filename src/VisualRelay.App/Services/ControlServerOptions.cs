using VisualRelay.Core.Configuration;
using VisualRelay.Domain;

namespace VisualRelay.App.Services;

/// <summary>
/// Configuration for the localhost control server, resolved from environment
/// variables:
/// <list type="bullet">
/// <item><c>VR_CONTROL_DISABLE=1</c> turns the whole server off.</item>
/// <item><c>VR_CONTROL_PORT</c> overrides the default loopback port (8765);
///   an unparseable value falls back to the default. When explicitly set,
///   the control API is load-bearing: a bind conflict throws so the app
///   refuses to run as an undrivable zombie.</item>
/// <item><c>VR_CONTROL_TOKEN</c>, when set, requires a matching
///   <c>X-VR-Token</c> request header (else 401).</item>
/// </list>
/// </summary>
public sealed record ControlServerOptions(
    bool Enabled, int Port, string? Token,
    bool PortWasExplicitlySet = false,
    string? InstanceId = null,
    int Pid = 0,
    string StartedUtc = "",
    string Version = "")
{
    private const int DefaultPort = 8765;

    /// <summary>
    /// The actual port the control server bound, populated after
    /// <see cref="ControlServer.Start"/> succeeds. Defaults to <see cref="Port"/>
    /// so in-memory handler tests see the configured value.
    /// </summary>
    public int ControlPort { get; set; } = Port;

    public static ControlServerOptions FromEnvironment(IEnvironmentAccessor env)
    {
        var disabled = env.GetEnvironmentVariable("VR_CONTROL_DISABLE") == "1";

        var port = DefaultPort;
        var portExplicit = false;
        var portValue = env.GetEnvironmentVariable("VR_CONTROL_PORT");
        if (!string.IsNullOrWhiteSpace(portValue)
            && int.TryParse(portValue, out var parsed)
            && parsed is > 0 and <= 65535)
        {
            port = parsed;
            portExplicit = true;
        }

        var token = env.GetEnvironmentVariable("VR_CONTROL_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            token = null;
        }

        var instanceId = Guid.NewGuid().ToString("N") + "-" + Environment.ProcessId;

        return new ControlServerOptions(
            Enabled: !disabled,
            Port: port,
            Token: token,
            PortWasExplicitlySet: portExplicit,
            InstanceId: instanceId,
            Pid: Environment.ProcessId,
            StartedUtc: DateTime.UtcNow.ToString("o"),
            Version: VersionHelper.ReadInformationalVersion());
    }
}
