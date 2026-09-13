using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Cli.Gates;

/// <summary>
/// The Windows analogue of <see cref="NonoGate"/>: on Windows the sandbox is nono
/// inside a WSL2 distro, so the launch needs WSL, a WSL2 distro, nono resolvable
/// inside it and Landlock active there. Pure: decides from a <see cref="WslProbe"/>,
/// names the FIRST failing check with its exact fix, and always ends with the
/// toolchain consequence, because the loud part of this design is that a Windows-only
/// toolchain cannot be driven any more.
/// </summary>
public static class WslGate
{
    /// <summary>The nono release the distro install instructions pin (matches the Nix and Homebrew pins).</summary>
    private const string PinnedNonoVersion = "0.75.0";

    private const string SuggestedDistro = "Ubuntu";

    /// <summary>0 with no message when the probe is usable; else 127 with the message to print.</summary>
    public static (int ExitCode, string? Message) Decide(WslProbe probe)
    {
        if (probe.IsUsable)
            return (0, null);

        var (problem, fix) = FirstFailingCheck(probe);
        var message = $"visual-relay: {problem}\n\n{Indent(fix)}";
        if (!string.IsNullOrWhiteSpace(probe.Diagnostics))
            message += $"\n\n{Indent("Probe details:\n" + probe.Diagnostics)}";
        message += $"\n\n{Indent(Consequence(probe.DistroName))}";
        return (127, message);
    }

    private static (string Problem, string Fix) FirstFailingCheck(WslProbe p)
    {
        if (!p.WslExeFound || p.WslPlatformMissing)
            return (p.WslExeFound ? "WSL is not installed." : "WSL is not installed (wsl.exe was not found).",
                $"Install WSL: run `wsl --install -d {SuggestedDistro}` in an elevated PowerShell and reboot. If "
                + $"`wsl -l -v` then lists no distro (a first install can bring WSL without one), run "
                + $"`wsl --install -d {SuggestedDistro}` again. Then run `wsl -d {SuggestedDistro}` once to create your "
                + "Linux user.");

        if (p.DistroName is null && p.RequestedDistro is not null)
            return ($"the WSL distro '{p.RequestedDistro}' selected by {WslProber.DistroEnvVar} is not installed "
                    + $"(installed: {Installed(p)}).",
                $"Install it with `wsl --install -d {p.RequestedDistro}`, or point {WslProber.DistroEnvVar} at one of "
                + "the installed distros (unset it to use the default).");

        if (p.DistroName is null)
            return ("no WSL distro is installed.",
                $"Install one: `wsl --install -d {SuggestedDistro}`, then run `wsl -d {SuggestedDistro}` once to create "
                + $"your Linux user. {WslProber.DistroEnvVar}=<name> selects a distro other than the default.");

        var d = p.DistroName;
        if (!p.IsWsl2)
            return ($"'{d}' is a WSL1 distro (kernel '{p.KernelRelease ?? "unknown"}'); the sandbox needs the WSL2 kernel.",
                $"Convert it with `wsl --set-version {d} 2`, or install a WSL2 distro with `wsl --install -d {SuggestedDistro}`. "
                + $"{WslProber.DistroEnvVar}=<name> selects a distro other than the default.");

        if (p.NonoPath is null)
            return ($"nono was not found inside the WSL distro '{d}'.",
                $"Install nono {PinnedNonoVersion} inside the distro: open it with `wsl -d {d}` and run "
                + $"`curl -fsSL https://nono.sh/install.sh | NONO_VERSION=v{PinnedNonoVersion} sh` (the installer "
                + "otherwise takes the latest release), or install the .deb from "
                + $"https://github.com/nolabs-ai/nono/releases/tag/v{PinnedNonoVersion} with "
                + $"`sudo apt install --no-install-recommends ./nono-cli_{PinnedNonoVersion}_amd64.deb`; then make sure "
                + $"a login shell finds it (`wsl -d {d} --exec sh -lc 'command -v nono'`).");

        if (!p.LandlockActive)
            return ($"Landlock is not active in the WSL distro '{d}'.",
                @"Remove a custom `kernel=` or `kernelCommandLine=` line from %UserProfile%\.wslconfig, then run "
                + "`wsl --update` and `wsl --shutdown`; the stock WSL2 kernel has enabled Landlock since 5.15.57.1.");

        return ($"the home directory of the default user in the WSL distro '{d}' could not be read.",
            $"Run `wsl -d {d}` once so the distro finishes its first-run user setup, then retry.");
    }

    private static string Consequence(string? distro) =>
        "Visual Relay on Windows runs your project's build and test commands inside the WSL2 distro "
        + (distro is null ? "you install" : $"'{distro}'")
        + "; install the toolchain there. A Windows-only toolchain (MSBuild against .NET Framework, "
        + "Visual Studio build tools, Unity on Windows, anything that needs an .exe) is not supported.";

    private static string Installed(WslProbe p) =>
        p.Distros.Count == 0 ? "none" : string.Join(", ", p.Distros.Select(d => d.Name));

    private static string Indent(string text) =>
        string.Join('\n', text.Split('\n').Select(line => line.Length == 0 ? line : "  " + line));
}
