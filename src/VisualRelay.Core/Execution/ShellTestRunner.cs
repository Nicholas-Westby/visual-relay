using System.Diagnostics;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed class ShellTestRunner : ITestRunner
{
    private readonly TimeSpan _timeout;
    public ShellTestRunner(TimeSpan? timeout = null) => _timeout = timeout ?? Timeout.InfiniteTimeSpan;
    public async Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        var result = await ProcessCapture.RunAsync("/bin/sh", $"-lc \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"", rootPath, _timeout, cancellationToken);
        var output = result.TimedOut
            ? $"test command timed out after {_timeout.TotalMilliseconds:F0}ms\n\n{result.Output}"
            : result.Output;
        return new TestRunResult(result.ExitCode, output, result.TimedOut);
    }
}
