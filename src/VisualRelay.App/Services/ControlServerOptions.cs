using VisualRelay.Core.Configuration;

namespace VisualRelay.App.Services;

/// <summary>
/// Configuration for the localhost control server, resolved from environment
/// variables:
/// <list type="bullet">
/// <item><c>VR_CONTROL_DISABLE=1</c> turns the whole server off.</item>
/// <item><c>VR_CONTROL_PORT</c> overrides the default loopback port (8765);
///   an unparseable value falls back to the default.</item>
/// <item><c>VR_CONTROL_TOKEN</c>, when set, requires a matching
///   <c>X-VR-Token</c> request header (else 401).</item>
/// </list>
/// </summary>
public sealed record ControlServerOptions(bool Enabled, int Port, string? Token)
{
    private const int DefaultPort = 8765;

    public static ControlServerOptions FromEnvironment(IEnvironmentAccessor env)
    {
        var disabled = env.GetEnvironmentVariable("VR_CONTROL_DISABLE") == "1";

        var port = DefaultPort;
        var portValue = env.GetEnvironmentVariable("VR_CONTROL_PORT");
        if (!string.IsNullOrWhiteSpace(portValue)
            && int.TryParse(portValue, out var parsed)
            && parsed is > 0 and <= 65535)
        {
            port = parsed;
        }

        var token = env.GetEnvironmentVariable("VR_CONTROL_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            token = null;
        }

        return new ControlServerOptions(Enabled: !disabled, Port: port, Token: token);
    }
}
