using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Stage-prompt assembly for the swival subagent. Split out of ProcessRunners.Helpers.cs
// so prompt construction (including the ## Verify output section) lives in one focused
// place. TrimForTail (the tail-window helper these share with the diagnostics extractors)
// stays in ProcessRunners.Helpers.cs.
public sealed partial class SwivalSubagentRunner
{
    internal static string BuildPrompt(StageInvocation invocation)
    {
        var parts = new List<string>
        {
            $"# Relay stage {invocation.Stage.Number}: {invocation.Stage.Name}",
            $"Task: {invocation.TaskName}",
            $"Working directory: {invocation.TargetRoot}",
            string.Empty,
            "## Task input",
            invocation.TaskInput,
            string.Empty,
            "## Manifest",
            invocation.Manifest.Count > 0 ? string.Join('\n', invocation.Manifest) : "(not set yet)"
        };
        if (!string.IsNullOrWhiteSpace(invocation.TaskContext))
        {
            parts.AddRange(["", "## Task context", invocation.TaskContext]);
        }

        if (invocation.LogSources.Count > 0)
        {
            parts.AddRange(["", "## Log sources", string.Join('\n', invocation.LogSources)]);
        }

        parts.AddRange(["", "## Prior stages", invocation.LedgerSoFar, "", invocation.Stage.OutputContract]);

        if (!string.IsNullOrWhiteSpace(invocation.LastTestOutput))
        {
            // The harness already ran the suite mechanically; this is its captured
            // output (TAIL kept — the Passed!/Failed: summary sits after the
            // sandbox/restore/build banner). Informational, not a re-run instruction.
            parts.AddRange(["", "## Verify output",
                "The harness already ran the test suite; its captured output (tail) is below."]);
            // Point at the persisted FULL log so the agent can scan the whole thing when
            // the tail isn't enough. The file is under the repo cwd (readable under the
            // sandbox's --allow-cwd grant). Placed BEFORE the tail so it isn't buried.
            if (!string.IsNullOrWhiteSpace(invocation.VerifyOutputPath))
            {
                parts.Add($"Full output: {invocation.VerifyOutputPath} — read it for the complete log.");
            }
            parts.Add(TrimForTail(invocation.LastTestOutput));
        }

        if (!string.IsNullOrWhiteSpace(invocation.TestCommand))
        {
            parts.AddRange(["", "## Verify command", "Run this exact command to reproduce and confirm the fix:", invocation.TestCommand]);
        }

        return string.Join('\n', parts);
    }

    private static string BuildCorrectivePrompt(StageInvocation invocation, string priorOutput, string? shapeError = null)
    {
        var problem = shapeError is not null
            ? $"The previous completion had a valid fenced JSON block but it was rejected: {shapeError}. " +
              "Reply with ONLY a corrected fenced JSON block — fix the issue, derive the values from the prior answer below. " +
              "Do NOT redo the work or add any other text."
            : "The previous completion was missing the required fenced JSON block. " +
              "Reply with ONLY that block — derive it from the prior answer below. " +
              "Do NOT redo the work or add any other text.";

        var parts = new List<string>
        {
            $"# Relay stage {invocation.Stage.Number}: {invocation.Stage.Name} — CORRECTIVE RETRY",
            $"Task: {invocation.TaskName}",
            string.Empty,
            problem,
            string.Empty,
            "## Expected contract",
            invocation.Stage.OutputContract,
            string.Empty,
            "## Prior output",
            priorOutput
        };
        return string.Join('\n', parts);
    }
}
