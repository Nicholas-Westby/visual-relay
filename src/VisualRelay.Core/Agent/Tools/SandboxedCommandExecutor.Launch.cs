using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

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
    // /bin/sh as one unparseable argument. On Windows the same /bin/sh runs inside
    // the distro, where the sandbox is.
    private const string ShellBinary = "/bin/sh";
    private const string ShellFlag = "-c";

    // Names the pid file a launch behind wsl.exe leaves in the distro.
    private const string WslLaunchTag = "tool";

    // Each command is spawned by ProcessCapture, which puts the child in its OWN
    // POSIX process group (setpgid(pid, pid)) and kills that group — SIGINT then
    // SIGKILL — on timeout or reap, so one command is independently killable and
    // leaves no orphans holding the pipes. Behind wsl.exe the tree is sampled and
    // stopped through the control instead, from inside the distro.
    private static readonly SandboxedCommandLauncher RealLauncher =
        async (fileName, arguments, workingDirectory, timeout, environment, environmentRemove, treeControl, cancellationToken) =>
        {
            var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
                fileName, arguments, workingDirectory, timeout, cancellationToken,
                environment, envRemove: environmentRemove, treeControl: treeControl);
            return new CommandRunOutcome(exitCode, output, timedOut);
        };

    // Builds the sandboxed launch. Never returns an unsandboxed program: on the local
    // host the command runs under nono; on Windows it runs under nono inside the WSL
    // distro (the workspace policy applies), or not at all, with the refusal as the
    // error. With no usable distro the launch is refused rather than silently accepted.
    private (SandboxedLaunch? Launch, string? Error) BuildLaunch(
        string targetRoot, AgentCommandGuard.Verdict verdict)
    {
        var host = Host;
        if (host is { IsWindows: true, Wsl: null })
            return (null, WslSandboxLauncher.BlockedMessage);

        var (program, programArguments) = verdict.Shell is { } shell
            ? (ShellBinary, new List<string> { ShellFlag, shell })
            : (verdict.Argv![0], verdict.Argv.Skip(1).ToList());

        // rollback: false — the agent path drops nono's rollback (see the type doc).
        // requestDiagnostics stays off: that JSON is a verify-artifact concern, and
        // its banner would be noise in the model's tool result.
        var prefix = SandboxedStage.BuildNonoPrefix(
            config, rollback: false, verboseDiagnostics: verboseDiagnostics, workspaceRoot: targetRoot, host: host);

        if (host.Wsl is { } context)
        {
            var (wsl, error) = WslSandboxLauncher.Build(
                context, targetRoot, [context.NonoPath, .. prefix], program, programArguments,
                SandboxedStage.BuildSandboxEnvironment(config), WslLaunchTag);
            return wsl is null ? (null, error) : (SandboxedLaunch.ForWsl(context, wsl), null);
        }

        var environment = SandboxedStage.BuildTargetCommandEnvironment(config);
        return (new SandboxedLaunch(
            NonoBinary, [.. prefix, program, .. programArguments], environment.Overrides, environment.Remove, null), null);
    }
}
