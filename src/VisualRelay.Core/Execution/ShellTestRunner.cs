using System.Diagnostics;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <param name="timeout">Time box for the command; infinite when omitted.</param>
/// <param name="loginShell">
/// Whether the Unix shell is a LOGIN shell (<c>-lc</c>). The default matches the
/// unsandboxed verify path. Pass <c>false</c> for <c>-c</c>, which inherits the
/// harness environment instead of re-resolving PATH from the user's profile — what
/// the sandboxed pipeline does, and therefore what candidate validation must do so a
/// command it accepts is the command that will later run.
/// </param>
/// <param name="host">
/// Where the shell runs from; null uses this machine. On Windows with a resolved
/// distro the command runs inside that distro, on the workspace the UNC root names,
/// so a command validated here is validated where the pipeline will run it.
/// </param>
/// <param name="environment">
/// Where the user's environment snapshot is looked up; null reads this process's variables.
/// </param>
public sealed class ShellTestRunner(
    TimeSpan? timeout = null, bool loginShell = true, SandboxHost? host = null,
    IEnvironmentAccessor? environment = null) : ITestRunner
{
    // Names the pid file a launch behind wsl.exe leaves in the distro.
    private const string WslLaunchTag = "bootstrap";

    // POSIX "command invoked cannot execute": a workspace the WSL policy refuses is a
    // launch that never happened, not a command that was not found (127).
    private const int RefusedExitCode = 126;

    private readonly TimeSpan _timeout = timeout ?? Timeout.InfiniteTimeSpan;

    public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        SandboxedLaunch launch;
        try
        {
            launch = ResolveLaunch(command, rootPath, loginShell, host ?? SandboxHost.Current, environment);
        }
        catch (InvalidOperationException refusal)
        {
            return new TestRunResult(RefusedExitCode, refusal.Message, false, sw.Elapsed);
        }

        var result = await ProcessCapture.RunAsync(
            launch.FileName, launch.Arguments, launch.StartIn(rootPath), _timeout, cancellationToken,
            environment: launch.Environment, envRemove: launch.EnvironmentRemove, treeControl: launch.TreeControl);
        var output = result.TimedOut
            ? $"test command timed out after {_timeout.TotalMilliseconds:F0}ms\n\n{result.Output}"
            : result.Output;
        return new TestRunResult(result.ExitCode, output, result.TimedOut, sw.Elapsed);
    }

    /// <summary>
    /// The unsandboxed shell launch for a user-authored test command, as
    /// <paramref name="host"/> runs it. Off a resolved distro it is
    /// <see cref="BuildShellLaunch"/>. Inside the distro it is <c>/bin/sh</c> through
    /// the same envelope the sandboxed launches use, with no nono in front: the
    /// envelope binds the command to the workspace loudly and its pid file lets a
    /// timeout stop the Linux tree. Throws <see cref="InvalidOperationException"/>
    /// with the refusal when the workspace cannot be used inside the distro.
    /// </summary>
    internal static SandboxedLaunch ResolveLaunch(
        string command, string rootPath, bool loginShell, SandboxHost host,
        IEnvironmentAccessor? accessor = null, IReadOnlyDictionary<string, string>? processEnv = null)
    {
        if (host.Wsl is not { } context)
        {
            // On Windows every sandboxed launch and every workspace git call runs
            // inside the distro, so a command checked here through cmd.exe would be
            // checked where the pipeline will never run it — and, worse, would run the
            // user's own test command on the Windows host with no sandbox at all.
            if (host.IsWindows)
                throw new InvalidOperationException(WslGate.Decide(WslContextResolver.UnusableProbe).Message!);

            // The environment the pipeline gives the command, the user's own from the snapshot: through
            // the dev launcher the check otherwise ran Ocelot's dotnet test under the launcher's nix SDK.
            var (fileName, arguments) = BuildShellLaunch(command, loginShell);
            var environment = SandboxedStage.BuildTargetCommandEnvironment(
                RelayConfigLoader.Defaults(command), accessor, processEnv);
            return new SandboxedLaunch(fileName, arguments, environment.Overrides, environment.Remove, null);
        }

        var (launch, refusal) = WslSandboxLauncher.Build(
            context, rootPath, [], "/bin/sh", [loginShell ? "-lc" : "-c", command], null, WslLaunchTag);
        return launch is null ? throw new InvalidOperationException(refusal) : SandboxedLaunch.ForWsl(context, launch);
    }

    /// <summary>
    /// The native shell launch for a user-authored test command off Windows:
    /// <c>/bin/sh -lc &lt;command&gt;</c>, the command being one argv entry the shell
    /// parses. There used to be a Windows arm that wrote the command to a temp batch
    /// file and ran it as <c>cmd.exe /c</c>; it ran the user's command on the Windows
    /// host with no sandbox, and it checked it where the pipeline would never run it,
    /// so a Windows host without a distro is refused before reaching here.
    /// <paramref name="loginShell"/> selects <c>-lc</c> or <c>-c</c>.
    /// </summary>
    internal static (string FileName, IReadOnlyList<string> Arguments) BuildShellLaunch(
        string command, bool loginShell = true) =>
        ("/bin/sh", [loginShell ? "-lc" : "-c", command]);
}
