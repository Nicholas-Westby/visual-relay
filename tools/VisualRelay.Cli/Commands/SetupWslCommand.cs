using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Cli.Commands;

/// <summary>
/// <c>setup-wsl</c>: sets up the WSL2 side of the Windows sandbox, which is a distro, a
/// Linux user, git and the pinned nono, without administrator rights. Running the command
/// is the consent: it says what it will do, then does it. WSL itself stays with the user,
/// because installing it takes administrator rights and a reboot.
/// </summary>
public static class SetupWslCommand
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count > 0)
        {
            Console.Error.WriteLine(
                $"usage: visual-relay setup-wsl (it sets up the WSL default distro, or the one {WslProber.DistroEnvVar} names)");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "visual-relay: setup-wsl sets up the sandbox on Windows. On macOS and Linux nono comes from the Nix devshell or Homebrew.");
            return 2;
        }

        return await WslSetup.RunAsync(WslSetupHost.ThisMachine(), Console.Error.WriteLine, CancellationToken.None);
    }
}
