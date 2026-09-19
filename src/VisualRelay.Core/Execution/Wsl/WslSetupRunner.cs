namespace VisualRelay.Core.Execution.Wsl;

/// <summary>How a setup run ended: <see cref="Failure"/> is null when every step succeeded.</summary>
public sealed record WslSetupOutcome(string? Failure);

/// <summary>
/// Carries out a <see cref="WslSetupPlan"/> through the host's wsl.exe runners, which have the
/// delegate shape <see cref="WslProber"/> takes: each step becomes its wsl.exe calls, run in
/// plan order (a step that <see cref="WslSetupStep.NeedsAdministrator"/> through the runner that
/// asks Windows for approval), and the first call that fails stops the run with a message
/// naming the step, the exit code and the end of its output. A distro install is also checked
/// against the distro listing, because its exit code does not say whether a distro arrived.
/// </summary>
public static class WslSetupRunner
{
    /// <summary>The <c>$0</c> every setup script runs under, so its errors say where they came from.</summary>
    private const string ScriptName = "vr-setup";

    private const int OutputTailLines = 20;

    /// <summary>How a Windows installer says it succeeded and needs a restart to finish.</summary>
    private const int SucceededRestartNeeded = 3010;

    public static async Task<WslSetupOutcome> RunAsync(
        WslSetupPlan plan, WslSetupHost host, Action<string> report, CancellationToken ct)
    {
        string? installedHere = null;
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            report($"[{i + 1}/{plan.Steps.Count}] {step.Description}");
            var run = step.NeedsAdministrator ? host.RunWslAsAdministrator : host.RunWsl;
            var output = "";
            foreach (var argv in CommandsFor(step, host.LocalNonoDeb))
            {
                (var exitCode, output) = await run(argv, ct);
                if (exitCode != 0 && !(step.NeedsAdministrator && exitCode == SucceededRestartNeeded))
                    return new WslSetupOutcome(Failure(i + 1, plan.Steps.Count, step, $"wsl.exe exited {exitCode}", output, installedHere));
            }

            if (step is not InstallDistroStep install)
                continue;
            // WSL exits 0 without installing anything when the distro has to wait for a restart
            // (WSL 2.7.14 on Windows 11 25H2, 2026-09-19, after it turned the Virtual Machine
            // Platform back on), so only the listing says whether the distro is there.
            if (!await IsListedAsync(host, install.Name, ct))
                return new WslSetupOutcome(Failure(i + 1, plan.Steps.Count, step,
                    $"wsl.exe exited 0 but did not install '{install.Name}'", output, installedHere));
            installedHere = install.Name;
        }

        return new WslSetupOutcome(null);
    }

    private static async Task<bool> IsListedAsync(WslSetupHost host, string distro, CancellationToken ct)
    {
        var (_, listing) = await host.RunWsl(["-l", "-v"], ct);
        return WslListParser.Parse(listing).Any(listed => listed.Name.Equals(distro, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string[]> CommandsFor(WslSetupStep step, string? localNonoDeb) => step switch
    {
        InstallWslStep => [["--install", "--no-distribution"]],
        InstallDistroStep s when s.Image == s.Name => [["--install", s.Image, "--no-launch"]],
        InstallDistroStep s => [["--install", s.Image, "--name", s.Name, "--no-launch"]],
        // A new default user takes effect only once the distro starts again.
        CreateUserStep s => [AsRoot(s.Distro, WslSetupScripts.CreateUser, s.User), ["--terminate", s.Distro]],
        InstallPackagesStep s => [AsRoot(s.Distro, WslSetupScripts.Packages)],
        InstallNonoStep s =>
        [
            AsRoot(s.Distro, WslSetupScripts.Nono, NonoRelease.Version, NonoRelease.Amd64DebSha256,
                NonoRelease.Arm64DebSha256, NonoRelease.DebUrlPrefix, localNonoDeb ?? ""),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, "setup has no commands for this step"),
    };

    private static string[] AsRoot(string distro, string script, params string[] args) =>
        ["-d", distro, "-u", "root", "--exec", "sh", "-c", script, ScriptName, .. args];

    private static string Failure(int number, int count, WslSetupStep step, string what, string output, string? installedHere)
    {
        var message = $"visual-relay: WSL setup stopped at step {number} of {count} ({step.Description}): {what}.";
        var tail = output.Replace("\r", "").Split('\n').Select(line => line.TrimEnd()).Where(line => line.Length > 0)
            .TakeLast(OutputTailLines).ToList();
        if (tail.Count > 0)
            message += "\n\n" + string.Join('\n', tail.Select(line => "    " + line));

        // A distro this run installed holds nothing of the user's yet, so starting over is safe
        // to suggest; for any other distro that advice would delete their work.
        message += installedHere is null
            ? "\n\n  Fix what the output above names, then run `.\\visual-relay.cmd setup-wsl` again: it does only what is still missing."
            : $"\n\n  The distro '{installedHere}' was installed by this run. To start over, remove it with "
              + $"`wsl --unregister {installedHere}` (this deletes everything in it), then run `.\\visual-relay.cmd setup-wsl` again.";
        return message;
    }
}
