using System.Text.RegularExpressions;
using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Sandbox-enforcing <see cref="ITestRunner"/> wrapper.  Transforms the command
/// into a <c>nono run -p vr-guard --allow-cwd --</c> invocation — without
/// <c>--rollback</c> / <c>--no-rollback-prompt</c> — so verification (test,
/// guard, bootstrap, new-guard probe) runs inside the same nono sandbox as the
/// agent with the same allowlist.  The shared <c>BuildNonoPrefix</c> builder
/// keeps the agent and verification prefixes in lockstep; they differ only
/// in the rollback flag pair.  The sandbox is always on — there is no opt-out.
/// On Windows the same nono runs inside the resolved WSL distro, on the Linux
/// workspace behind the UNC root, with the tree watched from inside the distro.
/// </summary>
/// <param name="inner">Decides the shape of the inner command: a shell line or a direct exec.</param>
/// <param name="config">Supplies the sandbox allow-list, the timeouts and the target environment.</param>
/// <param name="verboseDiagnostics">Output-only: shows nono's own banner instead of <c>--silent</c>.</param>
/// <param name="timeProvider">Clock for the watchdog. Null uses system time.</param>
/// <param name="host">Where the sandbox is launched from. Null uses this machine.</param>
public sealed partial class SandboxedTestRunner(
    ITestRunner inner, RelayConfig config, bool verboseDiagnostics = false,
    TimeProvider? timeProvider = null, SandboxHost? host = null) : ITestRunner
{
    // Names the pid file a launch behind wsl.exe leaves in the distro.
    private const string WslLaunchTag = "verify";

    private static readonly IReadOnlyDictionary<string, string> NoSearchPaths = new Dictionary<string, string>();

    private readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // Resolved on use, not at construction: on Windows the first resolution probes the machine.
    private SandboxHost Host => host ?? SandboxHost.Current;

    public Task<TestRunResult> RunAsync(
        string rootPath, string command, CancellationToken cancellationToken = default) =>
        RunAsync(rootPath, command, NoSearchPaths, cancellationToken);

    /// <inheritdoc />
    public async Task<TestRunResult> RunAsync(
        string rootPath, string command, IReadOnlyDictionary<string, string> searchPaths,
        CancellationToken cancellationToken)
    {
        var launch = ResolveSandboxedLaunch(command, rootPath, searchPaths);

        // Wrap the sandboxed run with the idle-reap watchdog. The wrapper (nono)
        // supervises the test process tree and can outlive the FINISHED tests —
        // orphaned testhost / MSBuild node-reuse workers keep nono alive — so a
        // plain wait rides TestTimeoutMilliseconds even when the tests passed in
        // seconds. RunWatchedAsync reaps once the tree goes output-silent +
        // CPU-idle and surfaces the inner command's real red/green result.
        var result = await RunWatchedAsync(
            launch.FileName, launch.Arguments, launch.StartIn(rootPath), launch.Environment,
            firstOutputTimeoutMs: config.TestIdleGraceMilliseconds,
            idleGraceMs: config.TestIdleGraceMilliseconds,
            hardCap: _timeout,
            cpuSampleIntervalMs: CpuPulseSampleIntervalMs,
            cancellationToken, _timeProvider,
            envRemove: launch.EnvironmentRemove, treeControl: launch.TreeControl);

        // Post-process: extract and strip the --diagnostics-json session object
        // from the captured output so callers see clean command output only.
        if (NonoDiagnosticsJsonParser.TryExtractDenials(result.Output, out var stripped, out var denials))
        {
            return result with { Output = stripped, Denials = denials };
        }

        return result;
    }

    /// <summary>
    /// The launch target (FileName, Arguments) for the given command. Exposed as
    /// internal for unit-test argument-shape assertions; the full launch is
    /// <see cref="ResolveSandboxedLaunch"/>.
    /// </summary>
    internal (string FileName, IReadOnlyList<string> Arguments) ResolveLaunch(string command, string? rootPath = null)
    {
        var launch = ResolveSandboxedLaunch(command, rootPath);
        return (launch.FileName, launch.Arguments);
    }

    /// <summary>
    /// Resolves the whole launch. On the local host it is nono wrapping the inner
    /// command with the target environment; on Windows it is wsl.exe running nono
    /// inside the distro on the Linux workspace <paramref name="rootPath"/> (the
    /// UNC share) names, or an <see cref="InvalidOperationException"/> carrying the
    /// refusal (no usable distro, a workspace the policy refuses). There is no
    /// unsandboxed fallback on either.
    /// </summary>
    internal SandboxedLaunch ResolveSandboxedLaunch(
        string command, string? rootPath = null, IReadOnlyDictionary<string, string>? searchPaths = null)
    {
        var sandboxHost = Host;
        if (sandboxHost is { IsWindows: true, Wsl: null })
            throw new InvalidOperationException(WslSandboxLauncher.BlockedMessage);

        // Sandbox always on: wrap in nono. verboseDiagnostics is output-only (--silent
        // when quiet); it never changes what the sandbox enforces.
        // requestDiagnostics: true requests --diagnostics-json so denial records are
        // captured and surfaced in verify artifacts — the verification path ONLY.
        var prefix = SandboxedStage.BuildNonoPrefix(
            config, rollback: false, verboseDiagnostics: verboseDiagnostics, workspaceRoot: rootPath,
            requestDiagnostics: true, host: sandboxHost);

        string program;
        IReadOnlyList<string> arguments;
        if (inner is ShellTestRunner)
        {
            // Non-login shell (-c, not -lc): the sandboxed verify must use the SAME toolchain the
            // harness/agent built with (inherited from the harness's environment), not whatever a
            // login shell re-resolves from the user's profile/PATH. A login shell here re-sourced a
            // different dotnet (e.g. ~/.dotnet) than the build's, causing runtime-mismatch launch
            // failures under nono. Inheriting the harness env keeps build and verify on one toolchain.
            //
            // -c and the command MUST be SEPARATE list entries. RunAsync feeds these to the
            // ProcessCapture IEnumerable<string> overload, which adds each entry to
            // ProcessStartInfo.ArgumentList verbatim — no quote-splitting. A merged
            // `-c "<command>"` entry would reach /bin/sh as one unparseable argument
            // ("/bin/sh: - : invalid option", exit 2), making every sandboxed verify falsely red.
            // ArgumentList re-quotes each entry as needed, so the command passes through unescaped.
            program = "/bin/sh";
            arguments = ["-c", WithSearchPaths(searchPaths, command)];
        }
        else
        {
            (program, arguments) = DirectExecTestRunner.ResolveLaunch(command);
        }

        if (sandboxHost.Wsl is { } context)
        {
            // Inside the distro the same /bin/sh -c runs (never a cmd.exe batch), and the
            // target environment travels in the argv: a wsl.exe child's Windows environment
            // does not cross into Linux, and the user-environment snapshot is a Unix concern.
            var (wsl, error) = WslSandboxLauncher.Build(
                context, rootPath ?? string.Empty, [context.NonoPath, .. prefix], program, arguments,
                SandboxedStage.BuildSandboxEnvironment(config), WslLaunchTag);
            return wsl is null ? throw new InvalidOperationException(error) : SandboxedLaunch.ForWsl(context, wsl);
        }

        var environment = SandboxedStage.BuildTargetCommandEnvironment(config);
        return new SandboxedLaunch("nono", [.. prefix, program, .. arguments], environment.Overrides, environment.Remove, null);
    }

    /// <summary>
    /// <paramref name="command"/> behind a shell prelude that sets each search-path variable to its
    /// value followed by the inherited one, when there is one, and exports it: the shell that runs
    /// the command is the only place that sees what the user's environment or the distro holds.
    /// </summary>
    internal static string WithSearchPaths(IReadOnlyDictionary<string, string>? searchPaths, string command)
    {
        if (searchPaths is not { Count: > 0 })
            return command;
        var prelude = string.Concat(searchPaths
            .Where(variable => ShellName().IsMatch(variable.Key))
            .OrderBy(variable => variable.Key, StringComparer.Ordinal)
            .Select(variable =>
                $"{variable.Key}='{variable.Value.Replace("'", @"'\''", StringComparison.Ordinal)}'\"${{{variable.Key}:+:${variable.Key}}}\"; export {variable.Key}; "));
        return prelude + command;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ShellName();
}
