using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Sandbox-enforcing <see cref="ITestRunner"/> wrapper.  Transforms the command
/// into a <c>nono run -p vr-guard --allow-cwd --</c> invocation — without
/// <c>--rollback</c> / <c>--no-rollback-prompt</c> — so verification (test,
/// guard, bootstrap, new-guard probe) runs inside the same nono sandbox as
/// Swival with the same allowlist.  The shared <c>BuildNonoPrefix</c> builder
/// keeps the Swival and verification prefixes in lockstep; they differ only
/// in the rollback flag pair.  The sandbox is always on — there is no opt-out.
/// </summary>
public sealed partial class SandboxedTestRunner(ITestRunner inner, RelayConfig config) : ITestRunner
{
    private readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(config.TestTimeoutMilliseconds);

    public async Task<TestRunResult> RunAsync(
        string rootPath, string command, CancellationToken cancellationToken = default)
    {
        var (fileName, args) = ResolveLaunch(command, rootPath);
        var env = SwivalSubagentRunner.BuildSandboxEnvironment(config);

        // Wrap the sandboxed run with the idle-reap watchdog. The wrapper (nono)
        // supervises the test process tree and can outlive the FINISHED tests —
        // orphaned testhost / MSBuild node-reuse workers keep nono alive — so a
        // plain wait rides TestTimeoutMilliseconds even when the tests passed in
        // seconds. RunWatchedAsync reaps once the tree goes output-silent +
        // CPU-idle and surfaces the inner command's real red/green result.
        return await RunWatchedAsync(
            fileName, args, rootPath, env,
            firstOutputTimeoutMs: config.TestIdleGraceMilliseconds,
            idleGraceMs: config.TestIdleGraceMilliseconds,
            hardCap: _timeout,
            cpuSampleIntervalMs: CpuPulseSampleIntervalMs,
            cancellationToken);
    }

    /// <summary>
    /// Resolves the launch target (FileName, Arguments) for the given command.
    /// Exposed as internal for unit-test argument-shape assertions. On Windows the
    /// verify command is wrapped in the OS-selected sandbox; <paramref name="rootPath"/>
    /// is the workspace the MXC policy confines writes to.
    /// </summary>
    internal (string FileName, IReadOnlyList<string> Arguments) ResolveLaunch(string command, string? rootPath = null)
    {
        if (OperatingSystem.IsWindows())
            return ResolveWindowsLaunch(command, rootPath);

        // Sandbox always on: wrap in nono.
        var prefix = SwivalSubagentRunner.BuildNonoPrefix(config, rollback: false);

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
            var args = new List<string>(prefix)
            {
                "/bin/sh",
                "-c",
                command
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

    // Windows verify launch: a shell command runs through cmd.exe /c, a script
    // through direct exec; the resulting program is then wrapped in MXC (default)
    // or run as-is under the degraded builtin opt-in, or blocked when no sandbox.
    private (string FileName, IReadOnlyList<string> Arguments) ResolveWindowsLaunch(string command, string? rootPath)
    {
        var (innerFile, innerArgs) = inner is ShellTestRunner
            ? ShellTestRunner.BuildShellLaunch(command, isWindows: true)
            : DirectExecTestRunner.ResolveLaunch(command);

        var (mode, wxc, policy) = MxcProvisioner.ResolvePlan(rootPath);
        return mode switch
        {
            WindowsSandboxMode.Mxc => WindowsSandbox.BuildMxcLaunch(wxc!, policy!, innerFile, innerArgs),
            // Builtin guards only swival's own file tools, not the verify command.
            WindowsSandboxMode.Builtin => (innerFile, innerArgs),
            _ => throw new InvalidOperationException(WindowsSandbox.BlockedMessage),
        };
    }
}
