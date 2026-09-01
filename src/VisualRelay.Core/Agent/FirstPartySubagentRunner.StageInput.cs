using System.Text;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Core.Agent;

public sealed partial class FirstPartySubagentRunner
{
    /// <summary>
    /// Writes the stage's <c>.input.json</c> beside its report and announces it,
    /// so the GUI's stage-input pane can show the prompt the moment a stage
    /// starts rather than after it ends.
    /// </summary>
    /// <param name="invocation">The stage being run.</param>
    /// <param name="prompt">The prompt the model was given.</param>
    /// <remarks>
    /// The Swival runner did this while assembling its argument list. That is
    /// not a property of running a subprocess — the pane reads the artifact
    /// either way — so it moved here rather than being lost with the runner.
    /// Best-effort throughout: a stage must never fail because a diagnostic
    /// artifact could not be written.
    /// </remarks>
    private void WriteStageInput(StageInvocation invocation, string prompt)
    {
        // A stage with no report file has nowhere to put the artifact. That is
        // every in-memory test double, and it is not an error.
        if (string.IsNullOrWhiteSpace(invocation.ReportFile)) return;

        try
        {
            var artifact = new StageInputArtifact(
                Version: 1,
                Stage: invocation.Stage.Number,
                Attempt: 1,
                Name: invocation.Stage.Name,
                SystemPrompt: invocation.Stage.SystemPrompt,
                InputPrompt: prompt,
                Timestamp: _timeProvider.GetUtcNow().ToString("O"));
            StageInputArtifact.Write(invocation.ReportFile, artifact);

            _relayEvents?.PublishAsync(
                new RelayEvent(
                    _timeProvider.GetUtcNow(), "info", "stage_input",
                    invocation.RunId, invocation.TargetRoot,
                    invocation.TaskName, invocation.Stage.Number,
                    invocation.Tier, 1,
                    Data: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["systemBytes"] = Encoding.UTF8.GetByteCount(artifact.SystemPrompt)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["inputBytes"] = Encoding.UTF8.GetByteCount(artifact.InputPrompt)
                            .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["path"] = StageInputArtifact.PathFor(invocation.ReportFile),
                    }),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            // Best-effort: a diagnostic artifact must never fail a stage.
        }
    }
}
