using System.Text.Json;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Shared setup for the command-family agent tools: one config, one executor wired
/// to a <see cref="RecordingCommandLauncher"/>, and the two little parsers that turn
/// a JSON literal into tool arguments and a duration into a <see cref="ToolContext"/>.
/// </summary>
internal static class CommandToolTestHarness
{
    /// <summary>The repository the tools are pointed at. Nothing is ever spawned in it.</summary>
    internal static string TargetRoot => Path.GetTempPath();

    /// <summary>Builds an executor whose launches land in <paramref name="launcher"/>.</summary>
    /// <param name="launcher">The recording double.</param>
    /// <param name="timeProvider">Clock for the reported duration. Null uses system time.</param>
    /// <returns>The executor.</returns>
    internal static SandboxedCommandExecutor Executor(
        RecordingCommandLauncher launcher, TimeProvider? timeProvider = null) =>
        new(Config(), launcher.Launcher, timeProvider: timeProvider);

    /// <summary>Parses a JSON literal into a tool's arguments.</summary>
    /// <param name="json">The arguments object.</param>
    /// <returns>The parsed element.</returns>
    internal static JsonElement Arguments(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>A context with <paramref name="remainingSeconds"/> left of the stage budget.</summary>
    /// <param name="remainingSeconds">What is left of the stage's wall clock.</param>
    /// <returns>The context.</returns>
    internal static ToolContext Context(double remainingSeconds) =>
        new(TargetRoot, TimeSpan.FromSeconds(remainingSeconds));

    private static RelayConfig Config() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int> { ["cheap"] = 90_000 },
            FirstOutputTimeoutMs: 660_000);
}
