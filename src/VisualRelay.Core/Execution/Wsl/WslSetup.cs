namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// The whole <c>setup-wsl</c> run: probe, plan, say what will happen, do it, then probe
/// again and let <see cref="WslGate"/> judge that second probe. A step can exit 0 and still
/// leave the sandbox unusable (a kernel without Landlock, say), so ready is never claimed
/// on the steps' exit codes alone.
/// </summary>
public static class WslSetup
{
    /// <summary>
    /// Names a nono Debian package on disk (a Windows or a Linux path) to install in place of
    /// the release download, for a distro that cannot reach GitHub. Its SHA-256 is checked
    /// against the pin all the same.
    /// </summary>
    public const string LocalNonoDebEnvVar = "VR_NONO_DEB";

    /// <summary>How long one wsl.exe call may take: a distro download on a slow line is the long one.</summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(30);

    /// <returns>0 when the distro is usable afterwards, 1 when a step failed, 127 when the gate still refuses.</returns>
    public static async Task<int> RunAsync(
        Func<CancellationToken, Task<WslProbe>> probe,
        Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> runWsl,
        string linuxUser,
        string? localNonoDeb,
        Action<string> write,
        CancellationToken ct)
    {
        var before = await probe(ct);
        var plan = WslSetupPlan.For(before, linuxUser);
        if (plan.Blocker is { } blocker)
        {
            write(blocker);
            return 127;
        }

        if (plan.Steps.Count == 0)
        {
            write($"visual-relay: WSL is ready: {Has(before)}. There is nothing to set up.");
            return 0;
        }

        write("visual-relay: setting up WSL for the sandbox. None of this needs administrator rights:\n"
              + string.Join('\n', plan.Steps.Select((step, i) => $"  {i + 1}. {step.Description}")));
        var outcome = await WslSetupRunner.RunAsync(plan, runWsl, write, localNonoDeb, ct);
        if (outcome.Failure is { } failure)
        {
            write(failure);
            return 1;
        }

        var after = await probe(ct);
        var (exitCode, message) = WslGate.Decide(after);
        write(exitCode == 0 ? Ready(after, plan) : message!);
        return exitCode;
    }

    /// <summary>
    /// Runs setup on this machine: its own probe (which honours <c>VR_WSL_DISTRO</c>), its
    /// wsl.exe, a Linux user named after the Windows one, and <see cref="LocalNonoDebEnvVar"/>.
    /// </summary>
    public static Task<int> RunOnThisMachineAsync(Action<string> write, CancellationToken ct)
    {
        var wslExe = WslContextResolver.FindWslExe();
        var localDeb = Environment.GetEnvironmentVariable(LocalNonoDebEnvVar);
        return RunAsync(
            WslContextResolver.ProbeAsync,
            // Without wsl.exe the probe blocks the plan, so nothing is ever run through this.
            (argv, token) => wslExe is null ? Task.FromResult((-1, "wsl.exe was not found")) : RunWslExeAsync(wslExe, argv, token),
            WslSetupPlan.LinuxUserFor(Environment.UserName),
            string.IsNullOrWhiteSpace(localDeb) ? null : localDeb.Trim(),
            write,
            ct);
    }

    private static async Task<(int ExitCode, string Output)> RunWslExeAsync(
        string wslExe, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            wslExe, argv, Path.GetTempPath(), StepTimeout, ct, environment: WslExeEnvironment.Variables);
        return timedOut
            ? (-1, output + $"\n[visual-relay: stopped this step after {StepTimeout.TotalMinutes:0} minutes]")
            : (exitCode, output);
    }

    private static string Ready(WslProbe probe, WslSetupPlan plan)
    {
        var message = $"visual-relay: WSL is ready: {Has(probe)}.";
        if (plan.Steps.OfType<CreateUserStep>().FirstOrDefault() is { } user)
            message += $"\n\n  Your Linux user is '{user.User}'. WSL signs you in without a password, so it has none; "
                       + $"to use sudo inside the distro, give it one with `wsl -d {user.Distro} -u root passwd {user.User}`.";
        return message;
    }

    private static string Has(WslProbe probe) =>
        $"'{probe.DistroName}' has {probe.NonoVersion ?? "nono"}, Landlock and git";
}
