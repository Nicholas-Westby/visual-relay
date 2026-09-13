using System.Text.RegularExpressions;

namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// Gathers a <see cref="WslProbe"/> by running wsl.exe through an injected runner
/// (the delegate owns the process and its environment, including
/// <c>WSL_UTF8=1</c>; tests script it and never spawn). Six steps: list the
/// distros (with none listed, <c>--version</c> tells a missing WSL from a missing
/// distro, and the probe stops); pick the one <c>VR_WSL_DISTRO</c> names, else the default; read
/// <c>uname -r</c>; resolve nono through a login shell (so a profile-added
/// <c>~/.cargo/bin</c> counts); read its version and ask it whether Landlock is up;
/// read the distro user's home; on a usable distro, read the PATH the user's login
/// shell builds. Every step after the listing runs with the
/// distro selected explicitly. A runner failure never throws out of the probe
/// (only cancellation does); it is recorded in <see cref="WslProbe.Diagnostics"/>.
/// </summary>
public static partial class WslProber
{
    /// <summary>Environment variable selecting a distro other than the WSL default.</summary>
    public const string DistroEnvVar = "VR_WSL_DISTRO";

    /// <summary>The kernel-release suffix Microsoft's stock WSL2 kernels carry.</summary>
    private const string Wsl2KernelMarker = "microsoft-standard-WSL2";

    /// <summary>The line nono's <c>setup --check-only</c> prints once its Landlock syscall probe succeeds.</summary>
    private const string LandlockEnabledLine = "Landlock enabled";
    private const int DiagnosticsCap = 400;

    /// <summary>Brackets the PATH the login shell prints, so what its rc files print around it is ignored.</summary>
    internal const string LoginPathMarker = "__VR_PATH__";

    /// <summary>
    /// Runs the distro user's login shell as an interactive login shell (a terminal's
    /// shell), which reads the profile and the whole rc file, and prints its PATH
    /// between markers. stdin is closed so an rc file that reads input cannot wait.
    /// </summary>
    internal const string LoginPathScript =
        "s=$(getent passwd \"$(id -un)\" | cut -d: -f7); "
        + "exec \"${s:-/bin/sh}\" -lic 'printf \"\\n" + LoginPathMarker + "%s" + LoginPathMarker + "\\n\" \"$PATH\"' </dev/null";

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static async Task<WslProbe> ProbeAsync(
        Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> runWsl,
        string? requestedDistro,
        string? wslExePath,
        CancellationToken ct)
    {
        var probe = WslProbe.Empty with { RequestedDistro = requestedDistro };
        if (wslExePath is null)
            return probe with { Diagnostics = "wsl.exe was not found on PATH or in the Windows system directory." };
        probe = probe with { WslExeFound = true, WslExePath = wslExePath };
        var notes = new List<string>();

        var listing = await RunAsync(runWsl, ["-l", "-v"], ct);
        var distros = WslListParser.Parse(listing.Output);
        probe = probe with { Distros = distros };
        if (distros.Count == 0)
        {
            notes.Add($"wsl -l -v (exit {listing.ExitCode}): {Compact(listing.Output)}");
            // The inbox wsl.exe of a Windows without WSL answers every command with an
            // install notice; only an installed WSL answers --version.
            var version = await RunAsync(runWsl, ["--version"], ct);
            return Finish(probe with { WslPlatformMissing = version.ExitCode != 0 }, notes);
        }

        var chosen = requestedDistro is null
            ? distros.FirstOrDefault(d => d.IsDefault) ?? distros[0]
            : distros.FirstOrDefault(d => d.Name.Equals(requestedDistro, StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            notes.Add($"{DistroEnvVar}='{requestedDistro}' names none of: {string.Join(", ", distros.Select(d => d.Name))}");
            return Finish(probe, notes);
        }

        var distro = chosen.Name;
        probe = probe with { DistroName = distro };

        var uname = await RunAsync(runWsl, ["-d", distro, "--exec", "uname", "-r"], ct);
        var kernel = FirstLine(uname.Output);
        // A listed version 2 is the authority (a custom `kernel=` is still a WSL2
        // VM); the marker rescues the verdict when the listing could not be read.
        probe = probe with
        {
            KernelRelease = kernel.Length == 0 ? null : kernel,
            IsWsl2 = chosen.Version == 2 || kernel.Contains(Wsl2KernelMarker, StringComparison.OrdinalIgnoreCase),
        };
        if (!probe.IsWsl2)
            notes.Add($"'{distro}' is listed as WSL {chosen.Version}; uname -r: {Compact(uname.Output)}");

        var which = await RunAsync(runWsl, ["-d", distro, "--exec", "sh", "-lc", "command -v nono"], ct);
        var nonoPath = which.ExitCode == 0 ? LastAbsoluteLine(which.Output) : null;
        probe = probe with { NonoPath = nonoPath };
        if (nonoPath is null)
        {
            notes.Add($"command -v nono (exit {which.ExitCode}): {Compact(which.Output)}");
        }
        else
        {
            var version = await RunAsync(runWsl, ["-d", distro, "--exec", nonoPath, "--version"], ct);
            var versionText = FirstLine(version.Output);
            probe = probe with { NonoVersion = version.ExitCode == 0 && versionText.Length > 0 ? versionText : null };

            // Landlock is decided by nono's own syscall probe, never by /sys/kernel/security/lsm:
            // a systemd distro under WSL does not mount securityfs, so that file is missing while
            // Landlock is up. The update check is off so the probe never waits on the network.
            var check = await RunAsync(runWsl,
                ["-d", distro, "--exec", "env", "NONO_NO_UPDATE_CHECK=1", nonoPath, "setup", "--check-only"], ct);
            probe = probe with
            {
                LandlockActive = check.ExitCode == 0 && check.Output.Contains(LandlockEnabledLine, StringComparison.Ordinal),
            };
            if (!probe.LandlockActive)
                notes.Add($"nono setup --check-only (exit {check.ExitCode}): {Compact(check.Output)}");
        }

        var home = await RunAsync(runWsl, ["-d", distro, "--exec", "sh", "-lc", "printf %s \"$HOME\""], ct);
        var homePath = home.ExitCode == 0 ? LastAbsoluteLine(home.Output) : null;
        probe = probe with { DistroHome = homePath };
        if (homePath is null)
            notes.Add($"$HOME (exit {home.ExitCode}): {Compact(home.Output)}");

        if (probe.IsUsable)
        {
            var login = await RunAsync(runWsl, ["-d", distro, "--exec", "sh", "-c", LoginPathScript], ct);
            probe = probe with { UserPath = BetweenMarkers(login.Output) };
            if (probe.UserPath is null)
                notes.Add($"login shell PATH (exit {login.ExitCode}), launches keep the distro default: {Compact(login.Output)}");
        }

        return Finish(probe, notes);
    }

    /// <summary>The PATH between the first two markers, or null when it is absent or not a PATH.</summary>
    private static string? BetweenMarkers(string output)
    {
        var start = output.IndexOf(LoginPathMarker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += LoginPathMarker.Length;
        var end = output.IndexOf(LoginPathMarker, start, StringComparison.Ordinal);
        if (end < 0)
            return null;
        var path = output[start..end];
        return path.Contains('/') ? path : null;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(
        Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> runWsl,
        string[] argv,
        CancellationToken ct)
    {
        try
        {
            return await runWsl(argv, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static WslProbe Finish(WslProbe probe, List<string> notes) =>
        probe with { Diagnostics = notes.Count == 0 ? null : string.Join('\n', notes) };

    private static string FirstLine(string output) =>
        output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty;

    /// <summary>The last line that is an absolute Linux path: a login shell may print a banner first.</summary>
    private static string? LastAbsoluteLine(string output) =>
        output.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.StartsWith('/'));

    private static string Compact(string output)
    {
        // A notice written in UTF-16 (the inbox wsl.exe ignores WSL_UTF8) reaches the
        // UTF-8 reader with a NUL after every character; drop them so it reads.
        var text = Whitespace().Replace(output.Replace("\0", ""), " ").Trim();
        return text.Length <= DiagnosticsCap ? text : text[..DiagnosticsCap] + "…";
    }
}
