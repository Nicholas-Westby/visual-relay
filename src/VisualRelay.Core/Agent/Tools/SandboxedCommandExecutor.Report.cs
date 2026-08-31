namespace VisualRelay.Core.Agent.Tools;

public sealed partial class SandboxedCommandExecutor
{
    // Output cap. A single `find` or a full build log would otherwise crowd out the
    // model's context; the head keeps the invocation and the tail keeps the failure.
    private const int MaxOutputCharacters = 60_000;
    private const int HeadCharacters = 8_000;

    // Turns a finished (or killed) command into the text the model reads. A non-zero
    // exit is NOT a tool error — a red test run is a result the model must see and act
    // on, and marking it an error would feed the consecutive-error guardrail. Only a
    // timeout, a guard denial, a bad argument and a launch failure are errors.
    private static ToolResult Describe(
        CommandRunOutcome outcome, CommandTimeoutBudget budget, bool rewritten, TimeSpan elapsed)
    {
        var lines = new List<string>();
        if (rewritten)
            lines.Add(AgentCommandGuard.RewrittenNotice);

        var output = Truncate(outcome.Output);

        if (outcome.TimedOut)
        {
            // The timeout text already carries the explanation, so it is not repeated.
            lines.Add(budget.TimedOutMessage);
            lines.Add(output.Length == 0
                ? "No output was captured before the timeout."
                : $"Output captured before the timeout:\n{output}");
            return ToolResult.Error(string.Join("\n", lines));
        }

        // A reduced timeout is announced on the calls that SURVIVED it too: the model
        // has to learn the ceiling from a green result, not only from a failure.
        if (budget.ReductionExplanation is { } reduction)
            lines.Add($"Note: {reduction}");

        lines.Add($"exit code {outcome.ExitCode} "
            + $"(ran for {CommandTimeoutBudget.FormatSeconds(elapsed)}s of the "
            + $"{CommandTimeoutBudget.FormatSeconds(budget.Applied)}s applied timeout)");
        lines.Add(output.Length == 0 ? "(no output)" : output);

        return ToolResult.Ok(string.Join("\n", lines));
    }

    private static string Truncate(string output)
    {
        var text = output.TrimEnd('\r', '\n');
        if (text.Length <= MaxOutputCharacters)
            return text;

        var omitted = text.Length - MaxOutputCharacters;
        var tail = text[^(MaxOutputCharacters - HeadCharacters)..];
        return $"{text[..HeadCharacters]}\n\n[... {omitted} characters of output omitted ...]\n\n{tail}";
    }
}
