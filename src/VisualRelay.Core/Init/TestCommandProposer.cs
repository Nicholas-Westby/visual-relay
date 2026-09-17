using System.Text;
using System.Text.Json;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Core.Init;

/// <summary>One command bootstrap already tried, and what the machine said about it.</summary>
/// <param name="Command">The command as it was run.</param>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Output">Its output; only the head is shown to the proposer.</param>
public sealed record CommandAttempt(string Command, int ExitCode, string Output);

/// <summary>
/// Asks a small agent run for the command that runs this project's tests, when none
/// of bootstrap's own candidates passed and the table knows no marker in the folder.
/// <para>
/// Every repository that defeated the table so far has been answered with more
/// per-language rules, which go out of date and help one ecosystem. This is the
/// general fallback instead: the evidence for a test command lives INSIDE files — the
/// CI workflow, a script's text, the README — so the proposer is an agent run with the
/// normal tool catalog that can read them and try its answer before giving it. What
/// makes a guess safe to keep is the check bootstrap already applies to its own
/// candidates, and the proposal goes through exactly that.
/// </para>
/// </summary>
internal static class TestCommandProposer
{
    /// <summary>Enough turns to read a CI file, a README and a build file, and try a command.</summary>
    internal const int MaxTurns = 30;

    internal const string SystemPrompt =
        "Find the ONE shell command that, run from the repository root, runs this project's unit "
        + "tests once and exits. Not a watch mode, not a whole CI pipeline, not a lint or format "
        + "step. Look at what the project itself runs: its CI configuration, its README, its build "
        + "and package files, and any scripts they point at. You may run commands to check your "
        + "answer, and you should. Do NOT edit, create or delete any file. Answer with the command "
        + "and the evidence you based it on.";

    internal const string OutputContract =
        """{ "testCmd": string, "evidence": string }""";

    /// <summary>
    /// Runs the proposer and returns the command it proposed, or null when it produced
    /// nothing usable. Never throws except on cancellation: a bootstrap that cannot ask
    /// falls back to the placeholder rather than failing.
    /// </summary>
    /// <param name="rootPath">The repository to look at.</param>
    /// <param name="attempts">What bootstrap already tried, and how it failed.</param>
    /// <param name="config">The configuration the run's budgets come from.</param>
    /// <param name="runner">The agent to run.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The proposed command, or null.</returns>
    internal static async Task<string?> RunAsync(
        string rootPath,
        IReadOnlyList<CommandAttempt> attempts,
        RelayConfig config,
        ISubagentRunner runner,
        CancellationToken ct)
    {
        var traceDirectory = Path.Combine(rootPath, ".relay", "bootstrap");
        Directory.CreateDirectory(traceDirectory);

        var invocation = new StageInvocation(
            Stage: new RelayStageDefinition(
                Number: 0,
                Name: "TestCommandProposer",
                Tier: "cheap",
                Kind: "llm",
                Files: "some",
                Commands: "all",
                SystemPrompt: SystemPrompt,
                OutputContract: OutputContract),
            Tier: "cheap",
            RunId: "bootstrap-" + DateTimeOffset.UtcNow.Ticks,
            TargetRoot: rootPath,
            TaskName: "test-command",
            TaskInput: BuildInput(attempts),
            LedgerSoFar: string.Empty,
            Manifest: [],
            LogSources: [],
            TraceDirectory: traceDirectory,
            ReportFile: Path.Combine(traceDirectory, "test-command.report.json"),
            MaxTurns: MaxTurns,
            AbsoluteCeilingMs: config.SubagentTimeoutMilliseconds);

        SubagentResult result;
        try
        {
            result = await runner.RunAsync(invocation, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }

        return result.IsValid ? ReadCommand(result.Json) : null;
    }

    /// <summary>
    /// What was tried and what the machine said. The first rejection's own output
    /// usually names the thing in the way — a project that must be excluded, a runner
    /// that found no tests — so the head of it is worth more than the reason alone.
    /// </summary>
    internal static string BuildInput(IReadOnlyList<CommandAttempt> attempts)
    {
        var text = new StringBuilder();
        text.AppendLine("Bootstrap could not find a working test command for this repository.");
        text.AppendLine();
        if (attempts.Count == 0)
        {
            text.AppendLine("It recognised no build or package file it knows, so it tried nothing.");
            return text.ToString();
        }

        text.AppendLine("These commands were tried and rejected:");
        foreach (var attempt in attempts)
        {
            text.AppendLine();
            text.AppendLine($"### `{attempt.Command}` (exit {attempt.ExitCode})");
            text.AppendLine();
            text.AppendLine("```");
            text.AppendLine(HeadOf(attempt.Output));
            text.AppendLine("```");
        }

        return text.ToString();
    }

    /// <summary>The first 30 lines: enough for a runner's refusal, short of a whole suite's output.</summary>
    private static string HeadOf(string output) =>
        string.Join('\n', output.Split('\n').Take(30)).TrimEnd();

    private static string? ReadCommand(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("testCmd", out var command)
                && command.GetString() is { } text
                && !string.IsNullOrWhiteSpace(text)
                    ? text.Trim()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
