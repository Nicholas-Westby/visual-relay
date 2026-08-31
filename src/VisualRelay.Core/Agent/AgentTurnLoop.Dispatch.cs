using System.Text.Json;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Llm;

namespace VisualRelay.Core.Agent;

public sealed partial class AgentTurnLoop
{
    /// <summary>
    /// Runs one tool call the model asked for, and returns the text it sees.
    /// <para>
    /// Every failure here is answered with a message the model can act on rather
    /// than an exception: an unknown name gets the nearest match, damaged
    /// arguments are repaired where that can be done without guessing, and a
    /// repeated call is warned about before it is ever refused.
    /// </para>
    /// </summary>
    private async Task<string> DispatchAsync(
        ToolCall call, LoopState state, ToolContext context, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            var message = UnknownToolRecovery.Describe(call.Name, [.. _tools.Keys]);
            Publish(AgentEventKind.UnknownTool, state.Turn, text: message, toolName: call.Name);
            state.RecordTool(call.Name, isError: true);
            return message;
        }

        var verdict = state.Storm.Observe(call.Name, call.Arguments);
        if (verdict != StormVerdict.Allow)
        {
            var message = state.Storm.Explain(verdict, call.Name);
            Publish(AgentEventKind.StormIntervention, state.Turn,
                text: message, toolName: call.Name, detail: verdict.ToString());

            if (verdict == StormVerdict.Suppress)
            {
                state.RecordTool(call.Name, isError: true);
                return message;
            }
        }

        var repair = ToolArgumentRepair.Repair(call.Arguments, tool.Definition.ParametersSchema);
        if (!repair.Succeeded)
        {
            var message = $"Could not read the arguments for {call.Name}: {repair.Repair}. "
                + "Send them again as a single well-formed JSON object.";
            state.RecordTool(call.Name, isError: true);
            return message;
        }

        if (repair.Repair is { Length: > 0 } note)
        {
            state.ArgumentRepairs++;
            Publish(AgentEventKind.ArgumentRepair, state.Turn, text: note, toolName: call.Name);
        }

        // The remaining stage budget is the only ceiling a tool ever sees. There
        // is no hidden per-call cap: the previous 240-second clamp could not be
        // raised and reported a number the model never asked for, so 378 calls
        // that asked for longer were silently cut and retried at the same value.
        var remaining = state.Deadline - _timeProvider.GetUtcNow();
        var callContext = context with
        {
            RemainingStageBudget = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
        };

        Publish(AgentEventKind.ToolCallStarted, state.Turn,
            toolName: call.Name, text: call.Arguments);

        var startedAt = _timeProvider.GetTimestamp();
        ToolResult result;
        try
        {
            using var document = JsonDocument.Parse(repair.Arguments!.ToJsonString());
            result = await tool
                .InvokeAsync(document.RootElement, callContext, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A tool that throws is a defect in the tool, not in the run. Report
            // it to the model and keep going; the stage still gets a report.
            result = ToolResult.Error($"{call.Name} failed unexpectedly: {ex.Message}");
        }

        var elapsed = _timeProvider.GetElapsedTime(startedAt);
        state.ToolSeconds += elapsed.TotalSeconds;
        state.RecordTool(call.Name, result.IsError);

        Publish(AgentEventKind.ToolCallFinished, state.Turn,
            toolName: call.Name, duration: elapsed,
            detail: result.IsError ? "error" : "ok");

        var prefix = verdict == StormVerdict.Warn
            ? state.Storm.Explain(verdict, call.Name) + "\n\n"
            : string.Empty;
        return prefix + result.Content;
    }
}
