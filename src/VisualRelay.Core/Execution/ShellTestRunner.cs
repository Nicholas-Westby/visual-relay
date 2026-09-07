using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
public sealed class ShellTestRunner(TimeSpan? timeout = null, bool loginShell = true) : ITestRunner
{
    private readonly TimeSpan _timeout = timeout ?? Timeout.InfiniteTimeSpan;
    public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var (fileName, args) = BuildShellLaunch(command, OperatingSystem.IsWindows(), loginShell);
        var result = await ProcessCapture.RunAsync(fileName, args, rootPath, _timeout, cancellationToken);
        var output = result.TimedOut
            ? $"test command timed out after {_timeout.TotalMilliseconds:F0}ms\n\n{result.Output}"
            : result.Output;
        return new TestRunResult(result.ExitCode, output, result.TimedOut, sw.Elapsed);
    }

    /// <summary>
    /// Resolves the OS-appropriate shell launch for a user-authored test command. On
    /// Unix it is <c>/bin/sh -lc &lt;command&gt;</c> (the command is one argv entry the
    /// shell parses). On Windows the command is written to a temp batch file and run
    /// as <c>cmd.exe /c &lt;batch&gt;</c>: passing the command itself as an argv entry
    /// would let .NET's argv quoting (which cmd.exe does not parse the same way)
    /// mangle quotes/metacharacters, so the batch file carries the command verbatim
    /// and only its clean path crosses the command line. <paramref name="isWindows"/>
    /// is injected so the dispatch is unit-testable on any OS;
    /// <paramref name="loginShell"/> selects <c>-lc</c> or <c>-c</c> on Unix.
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
