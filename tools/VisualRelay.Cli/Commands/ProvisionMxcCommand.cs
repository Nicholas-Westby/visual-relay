using VisualRelay.Core.Execution;

namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>visual-relay provision-mxc</c> — installs the Microsoft Execution Containers
/// runtime (wxc-exec) that confines task writes on Windows. Invoking the command IS
/// the consent to download the pinned, signed release; it is a no-op when wxc-exec is
/// already resolvable. Windows-only — macOS/Linux confine via nono.
/// </summary>
public static class ProvisionMxcCommand
{
    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("provision-mxc is Windows-only; macOS/Linux confine task writes via nono.");
            return 0;
        }

        // The user ran provision-mxc explicitly, so consent is given.
        var path = MxcInstaller.Ensure(consent: true, log: m => Console.Error.WriteLine(m));
        if (path is null)
        {
            Console.Error.WriteLine(
                "MXC provisioning did not complete; see messages above. Task execution stays "
                + "blocked until a sandbox is available (or set VR_WINDOWS_SANDBOX=builtin).");
            return 1;
        }

        Console.Error.WriteLine($"MXC ready at {path} — Windows task execution can confine writes.");
        return 0;
    }
}
