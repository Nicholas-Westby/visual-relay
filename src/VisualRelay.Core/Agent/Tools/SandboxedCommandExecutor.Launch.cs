using VisualRelay.Core.Execution;

namespace VisualRelay.Core.Agent.Tools;

public sealed partial class SandboxedCommandExecutor
{
    // The capability-sandbox wrapper. Every command tool is launched as
    // `nono run --profile <abs> --allow-cwd … -- <program> <args>`; the model's
    // program is never the launched file name.
    private const string NonoBinary = "nono";

    // Non-login shell (-c, not -lc): the sandboxed command must use the toolchain the
    // harness resolved, not whatever a login shell re-reads from the user's profile.
    // The flag and the command line MUST stay separate argv entries — ProcessCapture
    // adds each to ArgumentList verbatim, so a merged `-c "<command>"` would reach
    // /bin/sh as one unparseable argument.
    private const string ShellBinary = "/bin/sh";
    private const string ShellFlag = "-c";

    // Each command is spawned by ProcessCapture, which puts the child in its OWN
    // POSIX process group (setpgid(pid, pid)) and kills that group — SIGINT then
    // SIGKILL — on timeout or reap, so one command is independently killable and
    // leaves no orphans holding the pipes.
    private static readonly SandboxedCommandLauncher RealLauncher =
        async (fileName, arguments, workingDirectory, timeout, environment, environmentRemove, cancellationToken) =>
        {
            var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
                fileName, arguments, workingDirectory, timeout, cancellationToken,
                environment, envRemove: environmentRemove);
            return new CommandRunOutcome(exitCode, output, timedOut);
        };

    // Builds the sandboxed launch. Never returns an unsandboxed program: on Windows,
    // where nono does not exist, the command runs under Microsoft Execution
    // Containers or not at all — swival's degraded `builtin` sandbox does not guard a
    // raw command tool, so it is refused rather than silently accepted.
    private (string FileName, IReadOnlyList<string> Arguments, string? Error) BuildLaunch(
        string targetRoot, AgentCommandGuard.Verdict verdict)
    {
        var isWindows = OperatingSystem.IsWindows();

        var (program, programArguments) = verdict.Shell is { } shell
            ? BuildShellProgram(shell, isWindows)
            : (verdict.Argv![0], verdict.Argv.Skip(1).ToList());

        if (isWindows)
        {
            var (mode, wxcExec, policyPath) = MxcProvisioner.ResolvePlan(targetRoot);
            if (mode != WindowsSandboxMode.Mxc || wxcExec is null || policyPath is null)
                return (string.Empty, [], WindowsSandbox.BlockedMessage);

            var (windowsFile, windowsArguments) =
                WindowsSandbox.BuildMxcLaunch(wxcExec, policyPath, program, programArguments);
            return (windowsFile, windowsArguments, null);
        }

        // rollback: false — the agent path drops nono's rollback (see the type doc).
        // requestDiagnostics stays off: that JSON is a verify-artifact concern, and
        // its banner would be noise in the model's tool result.
        var arguments = new List<string>(SwivalSubagentRunner.BuildNonoPrefix(
            config, rollback: false, verboseDiagnostics: verboseDiagnostics, workspaceRoot: targetRoot))
        {
            program,
        };
        arguments.AddRange(programArguments);

        return (NonoBinary, arguments, null);
    }

    private static (string Program, List<string> Arguments) BuildShellProgram(string shell, bool isWindows)
    {
        if (!isWindows)
            return (ShellBinary, [ShellFlag, shell]);

        var (fileName, arguments) = ShellTestRunner.BuildShellLaunch(shell, isWindows: true);
        return (fileName, [.. arguments]);
    }
}
