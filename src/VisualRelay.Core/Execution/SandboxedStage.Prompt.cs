using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Stage-prompt assembly for the swival subagent. Split out of ProcessRunners.Helpers.cs
// so prompt construction (including the ## Verify output section) lives in one focused
// place. TrimForTail (the tail-window helper these share with the diagnostics extractors)
// stays in ProcessRunners.Helpers.cs.
public static partial class SandboxedStage
{
    /// <summary>
    /// How many manifest entries reach the prompt.
    /// <para>
    /// A very large repository can produce a manifest of thousands of paths.
    /// Pasting all of them spends the context window on a file listing before
    /// the model has read a line of code, and on the smallest window in the
    /// catalog that is most of the budget. The count is always stated, so the
    /// model knows the list was cut rather than that the repo is small.
    /// </para>
    /// </summary>
    internal const int MaxManifestEntriesInPrompt = 100;

    /// <summary>
    /// The manifest as the prompt carries it, capped and counted.
    /// </summary>
    /// <param name="manifest">Every path the manifest names.</param>
    /// <returns>The prompt section text.</returns>
    private static string ManifestText(IReadOnlyList<string> manifest)
    {
        if (manifest.Count == 0) return "(not set yet)";
        if (manifest.Count <= MaxManifestEntriesInPrompt) return string.Join('\n', manifest);

        var shown = string.Join('\n', manifest.Take(MaxManifestEntriesInPrompt));
        var hidden = manifest.Count - MaxManifestEntriesInPrompt;
        return shown
            + $"\n… and {hidden} more (manifest has {manifest.Count} entries; "
            + "read what you need with the file tools rather than assuming this list is complete)";
    }

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
            ManifestText(invocation.Manifest)
        };
        if (!string.IsNullOrWhiteSpace(invocation.TasksDir))
        {
            // Right after "Working directory:" so every stage sees it before the task input.
            parts.Insert(3, $"Protected paths (queue bookkeeping — never part of this task's diff): {invocation.TasksDir}/, .relay/, .swival/\nWrite throwaway artifacts (screenshots, probes, temporary files) to .relay/scratch/.");
        }
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

        if (!string.IsNullOrWhiteSpace(invocation.FullTestCommand) &&
            !string.Equals(invocation.FullTestCommand, invocation.TestCommand, StringComparison.Ordinal))
        {
            parts.AddRange(["", "## Before you declare done",
                "Once the targeted command above passes, run the project's FULL test suite ONCE " +
                "to catch cross-cutting checks the targeted run cannot surface:",
                "- Project-wide guards (size budgets, perf ceilings, assertion ratchets)",
                "- Lint / style / format gates",
                "- Tests that enforce special standards or invariants",
                "- Test-ordering hazards that only appear in a full run",
                "",
                "Run this exact command for the full suite:",
                invocation.FullTestCommand,
                "",
                "If the full suite fails, fix the failures and re-run the full command until it exits 0. " +
                "Only then is the change complete. Do NOT weaken assertions, delete tests, or skip hooks " +
                "to make it pass."]);
        }

        return string.Join('\n', parts);
    }

}
