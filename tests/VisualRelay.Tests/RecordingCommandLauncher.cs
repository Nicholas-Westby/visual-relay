using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// A <see cref="SandboxedCommandLauncher"/> test double: records the launch it was
/// handed and returns a canned outcome. It never spawns anything, so the command
/// tools' timeout, guard and sandbox-shape behaviour can be asserted exactly,
/// including a timeout, without a real process or a real wait.
/// </summary>
/// <param name="exitCode">The exit code to report.</param>
/// <param name="output">The captured output to report.</param>
/// <param name="timedOut">Whether to report that the applied timeout fired.</param>
/// <param name="clock">Virtual clock to advance, so a test can pin the reported duration.</param>
/// <param name="elapsedSeconds">How far to advance <paramref name="clock"/> while "running".</param>
internal sealed class RecordingCommandLauncher(
    int exitCode = 0, string output = "", bool timedOut = false,
    ManualTimeProvider? clock = null, double elapsedSeconds = 0)
{
    /// <summary>The program that was launched — the sandbox wrapper, never the model's.</summary>
    internal string FileName { get; private set; } = string.Empty;

    /// <summary>Every argument handed to the wrapper.</summary>
    internal string[] Arguments { get; private set; } = [];

    /// <summary>The directory the command was given.</summary>
    internal string WorkingDirectory { get; private set; } = string.Empty;

    /// <summary>The timeout that was actually applied.</summary>
    internal TimeSpan Timeout { get; private set; }

    /// <summary>The environment overrides the launched process was given.</summary>
    internal IReadOnlyDictionary<string, string> Environment { get; private set; } =
        new Dictionary<string, string>();

    /// <summary>The control for a tree the host cannot see (behind wsl.exe), or null.</summary>
    internal IProcessTreeControl? TreeControl { get; private set; }

    /// <summary>How many times a command was launched.</summary>
    internal int Calls { get; private set; }

    /// <summary>The delegate to hand to <see cref="SandboxedCommandExecutor"/>.</summary>
    internal SandboxedCommandLauncher Launcher =>
        (fileName, arguments, workingDirectory, timeout, environment, _, treeControl, _) =>
        {
            FileName = fileName;
            Arguments = [.. arguments];
            WorkingDirectory = workingDirectory;
            Timeout = timeout;
            Environment = environment;
            TreeControl = treeControl;
            Calls++;
            clock?.Advance(TimeSpan.FromSeconds(elapsedSeconds));
            return Task.FromResult(new CommandRunOutcome(exitCode, output, timedOut));
        };

    /// <summary>The model's own command: everything after the sandbox prefix's <c>--</c>.</summary>
    internal IReadOnlyList<string> LaunchedCommand => Arguments[(Array.IndexOf(Arguments, "--") + 1)..];
}
