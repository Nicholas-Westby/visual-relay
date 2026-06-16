using System.Diagnostics;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Sandbox-aware <see cref="ITestRunner"/> wrapper.  When
/// <see cref="RelayConfig.BypassSandbox"/> is true, delegates directly to the
/// inner runner (the bypass checkbox is the only sanctioned no-sandbox path).
/// When the sandbox is enabled (the default), transforms the command into a
/// <c>nono run -p vr-guard --allow-cwd --</c> invocation — without
/// <c>--rollback</c> / <c>--no-rollback-prompt</c> — so verification (test,
/// guard, bootstrap, new-guard probe) runs inside the same nono sandbox as
/// Swival with the same allowlist.  The shared <c>BuildNonoPrefix</c> builder
/// keeps the Swival and verification prefixes in lockstep; they differ only
/// in the rollback flag pair.
/// </summary>
public sealed class SandboxedTestRunner : ITestRunner
{
    private readonly ITestRunner _inner;
    private readonly RelayConfig _config;
    private readonly TimeSpan _timeout;

    public SandboxedTestRunner(ITestRunner inner, RelayConfig config)
    {
        _inner = inner;
        _config = config;
        _timeout = TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds);
    }

    public async Task<TestRunResult> RunAsync(
        string rootPath, string command, CancellationToken cancellationToken = default)
    {
        if (_config.BypassSandbox)
            return await _inner.RunAsync(rootPath, command, cancellationToken);

        var (fileName, args) = ResolveLaunch(command);

        var sw = Stopwatch.StartNew();
        var env = SwivalSubagentRunner.BuildSandboxEnvironment(_config);
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            fileName, args, rootPath, _timeout, cancellationToken, environment: env);

        if (timedOut)
        {
            output = $"test command timed out after {_timeout.TotalMilliseconds:F0}ms\n\n{output}";
        }

        return new TestRunResult(exitCode, output, timedOut, sw.Elapsed);
    }

    /// <summary>
    /// Resolves the launch target (FileName, Arguments) for the given command.
    /// Exposed as internal for unit-test argument-shape assertions.
    /// </summary>
    internal (string FileName, IReadOnlyList<string> Arguments) ResolveLaunch(string command)
    {
        if (_config.BypassSandbox)
        {
            if (_inner is ShellTestRunner)
                return ("/bin/sh", new[] { $"-lc \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" });
            else
                return DirectExecTestRunner.ResolveLaunch(command);
        }

        // Sandbox enabled: wrap in nono.
        var prefix = SwivalSubagentRunner.BuildNonoPrefix(_config, rollback: false);

        if (_inner is ShellTestRunner)
        {
            var args = new List<string>(prefix)
            {
                "/bin/sh",
                $"-lc \"{command.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            };
            return ("nono", args);
        }
        else
        {
            var parts = DirectExecTestRunner.ResolveLaunch(command);
            var args = new List<string>(prefix) { parts.FileName };
            args.AddRange(parts.Arguments);
            return ("nono", args);
        }
    }
}
