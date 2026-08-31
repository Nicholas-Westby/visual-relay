namespace VisualRelay.Core.Agent.Tools;

/// <summary>What one launched command produced.</summary>
/// <param name="ExitCode">The child's exit code, or -1 when it was killed on timeout.</param>
/// <param name="Output">Interleaved stdout and stderr.</param>
/// <param name="TimedOut">True when the applied timeout fired and the child was killed.</param>
public readonly record struct CommandRunOutcome(int ExitCode, string Output, bool TimedOut);

/// <summary>
/// Spawns one ALREADY-SANDBOXED command. The caller has built the sandbox wrapper,
/// so an implementation must not add or remove one; the seam exists so a test can
/// assert the launch shape and drive timeout behaviour without spawning a process.
/// </summary>
/// <param name="fileName">The program to launch — the sandbox wrapper, never the model's program.</param>
/// <param name="arguments">The wrapper's arguments, ending with the model's command.</param>
/// <param name="workingDirectory">The directory the command runs in.</param>
/// <param name="timeout">The timeout that was actually applied.</param>
/// <param name="environment">Environment overrides for the child.</param>
/// <param name="environmentRemove">Environment names to strip from the child.</param>
/// <param name="cancellationToken">Cancels the call.</param>
/// <returns>What the command produced.</returns>
public delegate Task<CommandRunOutcome> SandboxedCommandLauncher(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    TimeSpan timeout,
    IReadOnlyDictionary<string, string> environment,
    IReadOnlySet<string> environmentRemove,
    CancellationToken cancellationToken);
