using System.Runtime.Versioning;
using Microsoft.Win32;

namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// What Windows says about the virtual machine WSL2 runs its Linux kernel in, read from the
/// registry. wsl.exe cannot be asked: <c>--version</c> and <c>--status</c> both exit 0 while
/// the platform is off, and only <c>--status</c>'s localized text says so (measured with WSL
/// 2.7.14 on Windows 11 25H2, 2026-09-19).
/// </summary>
/// <param name="Running">
/// True when the Host Compute Service (vmcompute) is installed, the check <c>wsl --status</c>
/// makes before it says WSL2 cannot start. The Virtual Machine Platform brings that service,
/// and only once Windows has restarted after the platform was turned on.
/// </param>
/// <param name="RestartPending">
/// True when Windows is waiting for a restart to finish installing or removing a component
/// (Component Based Servicing's <c>RebootPending</c> key), as it is after the platform is turned
/// on, and also after an update: the key is machine-wide and does not say which install is waiting.
/// </param>
public sealed record WslVmPlatform(bool Running, bool RestartPending)
{
    private const string VmComputeServiceKey = @"SYSTEM\CurrentControlSet\Services\vmcompute";

    private const string RestartPendingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";

    /// <summary>A platform that runs with nothing pending, as on every machine that runs a WSL2 distro.</summary>
    public static WslVmPlatform Ready { get; } = new(true, false);

    /// <summary>This machine's answer, read from its registry; off Windows there is nothing to read.</summary>
    public static WslVmPlatform ThisMachine() => OperatingSystem.IsWindows() ? From(KeyExists) : Ready;

    /// <summary>
    /// The answer <paramref name="keyExists"/> gives for each key, where null means the key could
    /// not be read. That counts as the answer that blocks nothing, so an unreadable registry
    /// never stops a machine whose WSL works.
    /// </summary>
    internal static WslVmPlatform From(Func<string, bool?> keyExists) =>
        new(Running: keyExists(VmComputeServiceKey) ?? true, RestartPending: keyExists(RestartPendingKey) ?? false);

    [SupportedOSPlatform("windows")]
    private static bool? KeyExists(string path)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
