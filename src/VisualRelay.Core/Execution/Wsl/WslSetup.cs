using System.ComponentModel;
using System.Diagnostics;

namespace VisualRelay.Core.Execution.Wsl;

/// <summary>What setup runs against: how to probe, how to run wsl.exe, and who to create.</summary>
/// <param name="Probe">Probes the machine; called before and after the steps.</param>
/// <param name="RunWsl">Runs one wsl.exe call, the delegate <see cref="WslProber"/> takes.</param>
/// <param name="RunWslAsAdministrator">Runs one wsl.exe call after the Windows administrator prompt.</param>
/// <param name="LinuxUser">The user to create in a distro setup installs.</param>
/// <param name="LocalNonoDeb">A nono package on disk to install in place of the download, or null.</param>
public sealed record WslSetupHost(
    Func<CancellationToken, Task<WslProbe>> Probe,
    Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> RunWsl,
    Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> RunWslAsAdministrator,
    string LinuxUser,
    string? LocalNonoDeb)
{
    /// <summary>What Windows reports when the user declines the administrator prompt.</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Names a nono Debian package on disk (a Windows or a Linux path) to install in place of
    /// the release download, for a distro that cannot reach GitHub. Its SHA-256 is checked
    /// against the pin all the same.
    /// </summary>
    private const string LocalNonoDebEnvVar = "VR_NONO_DEB";

    /// <summary>How long one wsl.exe call may take: a distro download on a slow line is the long one.</summary>
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(30);

    /// <summary>Where the run's <see cref="WslSetupLog"/> is, for a failure to point at; null when nothing is logged.</summary>
    public string? LogPath { get; init; }

    /// <summary>
    /// This machine: its own probe (which honours <c>VR_WSL_DISTRO</c>), its wsl.exe with every
    /// call recorded in <see cref="WslSetupLog.ThisMachine"/>, a Linux user named after the
    /// Windows one, and <see cref="LocalNonoDebEnvVar"/>.
    /// </summary>
    public static WslSetupHost ThisMachine()
    {
        var wslExe = WslContextResolver.FindWslExe();
        var localDeb = Environment.GetEnvironmentVariable(LocalNonoDebEnvVar);
        var log = WslSetupLog.ThisMachine();
        // Without wsl.exe the probe blocks the plan, so nothing is ever run through these.
        var missing = Task.FromResult((-1, "wsl.exe was not found"));
        return new WslSetupHost(
            WslContextResolver.ProbeAsync,
            log.Recording((argv, ct) => wslExe is null ? missing : RunWslExeAsync(wslExe, argv, ct), "wsl.exe"),
            log.Recording((argv, ct) => wslExe is null ? missing : RunWslExeAsAdministratorAsync(wslExe, argv, ct),
                "wsl.exe as administrator"),
            WslSetupPlan.LinuxUserFor(Environment.UserName),
            string.IsNullOrWhiteSpace(localDeb) ? null : localDeb.Trim())
        {
            LogPath = log.Path,
        };
    }

    /// <summary>
    /// Runs wsl.exe through the Windows administrator prompt (the <c>runas</c> verb), in a window
    /// of its own where the user watches its progress. Output cannot be captured that way, so a
    /// failure says how to see it, and a declined prompt says that nothing was installed.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunWslExeAsAdministratorAsync(
        string wslExe, IReadOnlyList<string> argv, CancellationToken ct)
    {
        // The shell takes one argument string; every argument setup passes here is a plain flag.
        if (argv.Any(arg => arg.Length == 0 || arg.Any(c => char.IsWhiteSpace(c) || c == '"')))
            throw new ArgumentException("administrator wsl.exe calls take plain flags only", nameof(argv));
        var arguments = string.Join(' ', argv);
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(wslExe, arguments) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return (ErrorCancelled, "The Windows administrator prompt was declined, so nothing was installed.");
        }

        if (process is null)
            return (-1, "Windows did not start wsl.exe.");
        using (process)
        {
            await process.WaitForExitAsync(ct);
            return (process.ExitCode,
                $"wsl.exe ran in a window of its own, so its output is not shown here; to see it, run `wsl {arguments}` in an elevated PowerShell.");
        }
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
}

/// <summary>
/// The whole <c>setup-wsl</c> run: probe, plan, say what will happen, do it, then probe
/// again and let <see cref="WslGate"/> judge that second probe. A step can exit 0 and still
/// leave the sandbox unusable (a kernel without Landlock, say), so ready is never claimed
/// on the steps' exit codes alone.
/// </summary>
public static class WslSetup
{
    /// <returns>
    /// 0 when the distro is usable afterwards, 1 when a step failed, 3 when WSL itself was
    /// installed and Windows needs a restart before the rest, 127 when the gate still refuses.
    /// </returns>
    public static async Task<int> RunAsync(WslSetupHost host, Action<string> write, CancellationToken ct)
    {
        var before = await host.Probe(ct);
        var plan = WslSetupPlan.For(before, host.LinuxUser);
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

        write($"visual-relay: setting up WSL for the sandbox. {Approval(plan)}:\n{Describe(plan)}");
        return (await CarryOutAsync(host, plan, write, ct)).ExitCode;
    }

    /// <summary>The plan's steps as a numbered list, for showing before anything runs.</summary>
    public static string Describe(WslSetupPlan plan) =>
        string.Join('\n', plan.Steps.Select((step, i) => $"  {i + 1}. {step.Description}"));

    /// <summary>What the plan asks of the user's rights, for the line that introduces it.</summary>
    public static string Approval(WslSetupPlan plan) => plan.Steps.Any(step => step.NeedsAdministrator)
        ? "Windows will ask you to approve this as an administrator"
        : "None of this needs administrator rights";

    /// <summary>Runs <paramref name="plan"/>, then probes again and reports the gate's verdict.</summary>
    /// <returns>The exit code <see cref="RunAsync"/> documents, and the fresh probe when it is usable.</returns>
    public static async Task<(int ExitCode, WslProbe? Ready)> CarryOutAsync(
        WslSetupHost host, WslSetupPlan plan, Action<string> write, CancellationToken ct)
    {
        var outcome = await WslSetupRunner.RunAsync(plan, host, write, ct);
        if (outcome.Failure is { } failure)
        {
            write(failure);
            return (1, null);
        }

        // WSL's first install needs a restart before WSL can run a distro, and until it answers
        // there is nothing to probe the distro half with, so the next run plans that half.
        if (plan.Steps.OfType<InstallWslStep>().Any())
        {
            write("visual-relay: WSL is installed. Restart Windows to finish its install, then run "
                  + "`.\\visual-relay.cmd setup-wsl` again to set up the distro, git and nono.");
            return (3, null);
        }

        var after = await host.Probe(ct);
        var (exitCode, message) = WslGate.Decide(after);
        write(exitCode == 0 ? Ready(after, plan) : message!);
        return exitCode == 0 ? (0, after) : (exitCode, null);
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
