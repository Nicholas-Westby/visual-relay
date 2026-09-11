using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
public sealed class ShellTestRunner(TimeSpan? timeout = null, bool loginShell = true, SandboxHost? host = null) : ITestRunner
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
            launch = ResolveLaunch(command, rootPath, loginShell, host ?? SandboxHost.Current);
        }
        catch (InvalidOperationException refusal)
        {
            return new TestRunResult(RefusedExitCode, refusal.Message, false, sw.Elapsed);
        }

        var result = await ProcessCapture.RunAsync(
            launch.FileName, launch.Arguments, rootPath, _timeout, cancellationToken,
            environment: launch.Environment, treeControl: launch.TreeControl);
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
    internal static SandboxedLaunch ResolveLaunch(string command, string rootPath, bool loginShell, SandboxHost host)
    {
        if (host.Wsl is not { } context)
        {
            var (fileName, arguments) = BuildShellLaunch(command, host.IsWindows, loginShell);
            return new SandboxedLaunch(
                fileName, arguments, FrozenDictionary<string, string>.Empty, FrozenSet<string>.Empty, null);
        }

        var (launch, refusal) = WslSandboxLauncher.Build(
            context, rootPath, [], "/bin/sh", [loginShell ? "-lc" : "-c", command], null, WslLaunchTag);
        return launch is null ? throw new InvalidOperationException(refusal) : SandboxedLaunch.ForWsl(context, launch);
    }

    /// <summary>
    /// Resolves the OS-appropriate native shell launch for a user-authored test
    /// command. On Unix it is <c>/bin/sh -lc &lt;command&gt;</c> (the command is one
    /// argv entry the shell parses). On Windows the command is written to a temp
    /// batch file and run as <c>cmd.exe /c &lt;batch&gt;</c>: passing the command
    /// itself as an argv entry would let .NET's argv quoting (which cmd.exe does not
    /// parse the same way) mangle quotes/metacharacters, so the batch file carries
    /// the command verbatim and only its clean path crosses the command line. This
    /// is the Windows fallback where no usable distro exists; with one, the command
    /// runs inside the distro instead. <paramref name="isWindows"/> is injected so the
    /// dispatch is unit-testable on any OS; <paramref name="loginShell"/> selects
    /// <c>-lc</c> or <c>-c</c> on Unix.
    /// </summary>
    internal static (string FileName, IReadOnlyList<string> Arguments) BuildShellLaunch(
        string command, bool isWindows, bool loginShell = true) =>
        isWindows
            ? ("cmd.exe", ["/c", WriteWindowsCommandBatch(command)])
            : ("/bin/sh", [loginShell ? "-lc" : "-c", command]);

    // Materializes the command into a temp .cmd named by its content hash (so an
    // identical command reuses one file — bounded, race-safe), and returns its path.
    private static string WriteWindowsCommandBatch(string command)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command)))[..16];
        var path = Path.Combine(Path.GetTempPath(), $"vr-verify-{hash}.cmd");
        // @echo off keeps the wrapper's output clean; the command's own exit code is
        // what cmd.exe /c returns.
        File.WriteAllText(path, "@echo off\r\n" + command + "\r\n");
        return path;
    }
}
