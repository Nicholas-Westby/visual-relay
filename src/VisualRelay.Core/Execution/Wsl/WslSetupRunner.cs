namespace VisualRelay.Core.Execution.Wsl;

/// <summary>How a setup run ended: <see cref="Failure"/> is null when every step succeeded.</summary>
public sealed record WslSetupOutcome(string? Failure);

/// <summary>
/// Carries out a <see cref="WslSetupPlan"/> through an injected wsl.exe runner, the same
/// delegate shape <see cref="WslProber"/> takes: each step becomes its wsl.exe calls, run in
/// plan order, and the first call that fails stops the run with a message naming the step,
/// the exit code and the end of its output.
/// </summary>
public static class WslSetupRunner
{
    /// <summary>The <c>$0</c> every setup script runs under, so its errors say where they came from.</summary>
    private const string ScriptName = "vr-setup";

    private const int OutputTailLines = 20;

    public static async Task<WslSetupOutcome> RunAsync(
        WslSetupPlan plan,
        Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> runWsl,
        Action<string> report,
        string? localNonoDeb,
        CancellationToken ct)
    {
        string? installedHere = null;
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];
            report($"[{i + 1}/{plan.Steps.Count}] {step.Description}");
            foreach (var argv in CommandsFor(step, localNonoDeb))
            {
                var (exitCode, output) = await runWsl(argv, ct);
                if (exitCode != 0)
                    return new WslSetupOutcome(Failure(i + 1, plan.Steps.Count, step, exitCode, output, installedHere));
            }

            if (step is InstallDistroStep install)
                installedHere = install.Name;
        }

        return new WslSetupOutcome(null);
    }

    private static IReadOnlyList<string[]> CommandsFor(WslSetupStep step, string? localNonoDeb) => step switch
    {
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

    private static string Failure(int number, int count, WslSetupStep step, int exitCode, string output, string? installedHere)
    {
        var message = $"visual-relay: WSL setup stopped at step {number} of {count} ({step.Description}): wsl.exe exited {exitCode}.";
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
