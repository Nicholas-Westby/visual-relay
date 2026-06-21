using System.Text;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class SwivalSubagentRunner
{
    /// <summary>
    /// Build the argument list with the assembled prompt, persist a
    /// <c>.input.json</c> artifact next to the report, and emit a
    /// <c>stage_input</c> event so the UI can surface the prompt immediately.
    /// Best-effort: a write or publish failure must never abort the run.
    /// </summary>
    private List<string> BuildPromptArguments(
        StageInvocation invocation,
        string resolvedCommands,
        string? correctivePriorOutput,
        string? correctiveShapeError,
        int attempt,
        string reportFile)
    {
        var arguments = BuildArguments(invocation, resolvedCommands);
        var inputPrompt = correctivePriorOutput is not null
            ? BuildCorrectivePrompt(invocation, correctivePriorOutput, correctiveShapeError)
            : BuildPrompt(invocation);
        arguments.Add(inputPrompt);

        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("O");
            var artifact = new StageInputArtifact(
                Version: 1,
                Stage: invocation.Stage.Number,
                Attempt: attempt,
                Name: invocation.Stage.Name,
                SystemPrompt: invocation.Stage.SystemPrompt,
                InputPrompt: inputPrompt,
                Timestamp: timestamp);
            StageInputArtifact.Write(reportFile, artifact);

            if (_eventSink is not null)
            {
                var inputPath = StageInputArtifact.PathFor(reportFile);
                _ = _eventSink.PublishAsync(new RelayEvent(
                    DateTimeOffset.UtcNow, "info", "stage_input",
                    invocation.RunId, invocation.TargetRoot,
                    invocation.TaskName, invocation.Stage.Number,
                    invocation.Tier, attempt,
                    Data: new Dictionary<string, string>
                    {
                        ["systemBytes"] = Encoding.UTF8.GetByteCount(artifact.SystemPrompt).ToString(),
                        ["inputBytes"] = Encoding.UTF8.GetByteCount(artifact.InputPrompt).ToString(),
                        ["path"] = inputPath
                    }), CancellationToken.None);
            }
        }
        catch
        {
            // best-effort: a write failure must not abort the run
        }

        return arguments;
    }
}
