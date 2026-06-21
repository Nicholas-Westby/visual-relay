using System.Net.NetworkInformation;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Best-effort "is there an ESTABLISHED TCP connection to the model backend?"
/// probe, derived from the OS connection table via
/// <see cref="IPGlobalProperties"/> (no subprocess, cross-platform). Used by the
/// stage watchdog as one corroborating signal of a SOCKET-WEDGED agent: a
/// subagent blocked on <c>recv()</c> from a dead litellm socket holds the
/// connection ESTABLISHED while producing zero output and burning ~0 CPU.
///
/// The host/port come solely from <see cref="ModelBackend.BaseUrl"/> (one source
/// of truth) — nothing about the backend endpoint is hardcoded here. Returns
/// false on any sampling failure: the watchdog must treat "can't tell" as "no
/// wedge evidence", never as a reason to kill.
/// </summary>
internal static class BackendSocketProbe
{
    /// <summary>
    /// True when at least one TCP connection in the OS table is ESTABLISHED to
    /// the backend's host:port (matched against <see cref="ModelBackend.BaseUrl"/>).
    /// The connection table is process-wide, not per-pid: this is deliberately a
    /// corroborating signal only, gated by the watchdog behind a full
    /// inactivity-window silence + an idle agent subtree, so a process-wide match
    /// can never by itself cause a kill.
    /// </summary>
    internal static bool HasEstablishedBackendConnection()
    {
        try
        {
            var uri = new Uri(ModelBackend.BaseUrl);
            return HasEstablishedConnectionTo(uri.Host, uri.Port);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pure-ish core: scans the active TCP connection table for an ESTABLISHED
    /// entry whose remote endpoint matches <paramref name="host"/>:<paramref name="port"/>.
    /// Host comparison is by parsed IP when both sides parse, else by string, so
    /// "127.0.0.1" and "localhost" forms compare on their literal endpoint text.
    /// </summary>
    private static bool HasEstablishedConnectionTo(string host, int port)
    {
        var connections = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections();
        System.Net.IPAddress? targetIp = System.Net.IPAddress.TryParse(host, out var parsed) ? parsed : null;

        foreach (var conn in connections)
        {
            if (conn.State != TcpState.Established)
                continue;
            if (conn.RemoteEndPoint.Port != port)
                continue;

            var remote = conn.RemoteEndPoint.Address;
            if (targetIp is not null)
            {
                if (remote.Equals(targetIp))
                    return true;
            }
            else if (string.Equals(remote.ToString(), host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
