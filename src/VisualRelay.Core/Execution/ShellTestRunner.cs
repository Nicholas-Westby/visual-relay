using System.Diagnostics;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed class ShellTestRunner(TimeSpan? timeout = null) : ITestRunner
{
    private readonly TimeSpan _timeout = timeout ?? Timeout.InfiniteTimeSpan;
    public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var (fileName, args) = BuildShellLaunch(command, OperatingSystem.IsWindows());
        var result = await ProcessCapture.RunAsync(fileName, args, rootPath, _timeout, cancellationToken);
        var output = result.TimedOut
            ? $"test command timed out after {_timeout.TotalMilliseconds:F0}ms\n\n{result.Output}"
            : result.Output;
        return new TestRunResult(result.ExitCode, output, result.TimedOut, sw.Elapsed);
    }

    /// <summary>
    /// Resolves the OS-appropriate shell launch for a user-authored test command:
    /// <c>cmd.exe /c &lt;command&gt;</c> on Windows, <c>/bin/sh -lc &lt;command&gt;</c>
    /// on Unix. The command is a single argv entry (no manual quoting) — the host
    /// shell parses it. <paramref name="isWindows"/> is injected so the dispatch is
    /// unit-testable on any OS.
    /// </summary>
    internal static (string FileName, IReadOnlyList<string> Arguments) BuildShellLaunch(
        string command, bool isWindows) =>
        isWindows
            ? ("cmd.exe", new[] { "/c", command })
            : ("/bin/sh", new[] { "-lc", command });
}
