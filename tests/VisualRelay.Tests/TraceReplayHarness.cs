using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.Core.Agent;
using VisualRelay.Core.Agent.Tools;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>What replaying one recorded trace through the new loop produced.</summary>
/// <param name="Expected">
/// The tool calls the recorded model made, excluding the ones named in
/// <see cref="TraceReplayHarness.NotPorted"/>.
/// </param>
/// <param name="Actual">The tool calls the new loop made.</param>
/// <param name="Outcome">How the new loop finished.</param>
/// <param name="UnportedCalls">
/// Recorded calls to a tool the new set deliberately does not have. Reported
/// rather than compared, and counted so the cost of dropping them is visible.
/// </param>
public sealed record TraceReplayResult(
    IReadOnlyList<RecordedToolCall> Expected,
    IReadOnlyList<RecordedToolCall> Actual,
    AgentLoopOutcome Outcome,
    IReadOnlyList<RecordedToolCall> UnportedCalls)
{
    /// <summary>True when the loop reproduced the recorded calls exactly, in order.</summary>
    public bool Matches =>
        Expected.Count == Actual.Count
        && Expected.Zip(Actual).All(pair =>
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal)
            && string.Equals(pair.First.Arguments, pair.Second.Arguments, StringComparison.Ordinal));

    /// <summary>The first difference, for a failure message.</summary>
    public string Describe()
    {
        if (Matches) return "matched";

        var limit = Math.Max(Expected.Count, Actual.Count);
        for (var i = 0; i < limit; i++)
        {
            var expected = i < Expected.Count ? $"{Expected[i].Name} {Expected[i].Arguments}" : "(nothing)";
            var actual = i < Actual.Count ? $"{Actual[i].Name} {Actual[i].Arguments}" : "(nothing)";
            if (expected != actual) return $"call {i}: recorded {expected}, replayed {actual}";
        }

        return "lengths differ";
    }
}

/// <summary>
/// Replays a recorded trace's model turns through the new loop and compares the
/// tool calls it makes against the ones the recorded model made.
/// <para>
/// This is the same shape as <c>ParityHarness</c>, which compares GitSim against
/// real git, applied to the agent. It is free and deterministic: the model side
/// is a recording, so nothing reaches a provider and nothing is spent. The spec
/// is explicit that nothing goes to a paid benchmark until this replays clean.
/// </para>
/// </summary>
public static class TraceReplayHarness
{
    /// <summary>
    /// The fourteen tools the new set offers. Every other name the recorded runs
    /// used is treated as unported and excluded from the comparison.
    /// </summary>
    public static readonly IReadOnlyList<string> PortedTools =
    [
        "read_file", "read_multiple_files", "write_file", "edit_file", "delete_file",
        "list_files", "grep", "outline", "view_image",
        "run_command", "run_shell_command", "think", "todo", "snapshot",
    ];

    /// <summary>
    /// Whether a recorded tool name survives into the new set.
    /// <para>
    /// Counted across all 967 recorded traces, the names that do NOT are:
    /// <c>python</c> 89, <c>use_skill</c> 55 (always the nono-sandbox skill),
    /// <c>check_subagents</c> 25, <c>fetch_url</c> 24, <c>spawn_subagent</c> 16,
    /// <c>bash</c> 4, <c>glob</c> 3, <c>find</c> 3, and three names the model
    /// invented once each. The spec calls skills and subagents unused; they are
    /// rare, not unused, so they are excluded EXPLICITLY and counted rather than
    /// allowed to fail the differential silently.
    /// </para>
    /// <para>
    /// <c>python</c> is dropped deliberately: its 89 calls are covered by the
    /// command tools, and routing them there is what closes the bypass that let
    /// all 89 run without ever meeting the command guard.
    /// </para>
    /// </summary>
    /// <param name="name">The recorded tool name.</param>
    /// <returns>True when the new set still offers it.</returns>
    public static bool IsPorted(string name) => PortedTools.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// Replays one trace.
    /// </summary>
    /// <param name="tracePath">The recorded transcript.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    /// <returns>The comparison.</returns>
    public static async Task<TraceReplayResult> ReplayAsync(
        string tracePath, CancellationToken cancellationToken = default)
    {
        var recorded = RecordedTrace.ReadTurns(tracePath);
        var all = recorded.SelectMany(turn => turn.ToolCalls).ToList();
        var expected = all.Where(call => IsPorted(call.Name)).ToList();
        var unported = all.Where(call => !IsPorted(call.Name)).ToList();

        // Replay only the turns the new loop could actually make, so a dropped
        // tool does not shift every later call and swamp a real difference.
        var ported = recorded
            .Select(turn => turn with
            {
                ToolCalls = [.. turn.ToolCalls.Where(call => IsPorted(call.Name))],
            })
            .ToList();

        var turns = Coalesce(ported);

        var transport = new RecordedTurnTransport(turns);
        var observed = new ObservingToolset();
        var loop = new AgentTurnLoop(
            new ChatCompletionClient(transport), observed.Tools, new NullAgentEventSink());

        var result = await loop.RunAsync(
            [new ChatMessage("user", "replay")],
            new AgentLoopOptions(
                Model: "recorded",
                Endpoint: new Uri("https://replay.invalid/v1/chat/completions"),
                Headers: new Dictionary<string, string>(),
                Capabilities: ProviderCapabilityCatalog.For("DeepSeek"),
                // Generous, so a long recorded trace is never cut short by a
                // budget the original run did not have.
                MaxTurns: Math.Max(4, turns.Count + 2),
                RetryBackoffBase: TimeSpan.Zero),
            new ToolContext(Path.GetTempPath(), TimeSpan.FromMinutes(5)),
            cancellationToken).ConfigureAwait(false);

        return new TraceReplayResult(expected, observed.Calls, result.Outcome, unported);
    }

    /// <summary>
    /// Translates the recorded transcript's turn boundaries into the ones the
    /// wire format uses.
    /// <para>
    /// The previous runner recorded a model's narration as its own assistant
    /// record, separate from the record carrying the tool calls that followed.
    /// In an OpenAI-shaped response those arrive together, and a turn with text
    /// and NO tool calls means the model has finished. Replaying the records
    /// one-to-one therefore ends the loop at the first piece of narration, which
    /// looks like a divergence but is only a difference of transcript format.
    /// Text is folded forward into the next tool-calling turn; only trailing
    /// narration becomes the final answer.
    /// </para>
    /// </summary>
    private static List<RecordedTurn> Coalesce(IReadOnlyList<RecordedTurn> recorded)
    {
        var turns = new List<RecordedTurn>();
        var pending = new StringBuilder();

        foreach (var turn in recorded)
        {
            if (turn.ToolCalls.Count == 0)
            {
                pending.Append(turn.Text);
                continue;
            }

            turns.Add(turn with { Text = pending + turn.Text });
            pending.Clear();
        }

        if (pending.Length > 0) turns.Add(new RecordedTurn(pending.ToString(), []));
        return turns;
    }

    /// <summary>Discards events; the replay compares calls, not the stream.</summary>
    private sealed class NullAgentEventSink : IAgentEventSink
    {
        public void Publish(AgentEvent agentEvent)
        {
            // Intentionally empty.
        }
    }

    /// <summary>Serves the recorded assistant turns back as SSE, one per request.</summary>
    private sealed class RecordedTurnTransport(IReadOnlyList<RecordedTurn> turns) : IProviderTransport
    {
        private int _next;

        public Task<ProviderResponse> SendAsync(
            ProviderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderResponse(200, new Dictionary<string, string>(), "{}"));

        public Task<ProviderStreamResponse> StreamAsync(
            ProviderRequest request, CancellationToken cancellationToken = default)
        {
            var body = _next < turns.Count ? Render(turns[_next++]) : RenderFinal();
            return Task.FromResult(new ProviderStreamResponse(
                200, new Dictionary<string, string>(), _ => Emit(body)));
        }

        private static string RenderFinal() =>
            Frame("""{"model":"recorded","choices":[{"delta":{"content":"done"},"finish_reason":null}]}""")
            + Frame("""{"model":"recorded","choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""")
            + "data: [DONE]\n\n";

        private static string Render(RecordedTurn turn)
        {
            var builder = new StringBuilder();

            if (turn.Text.Length > 0)
            {
                var delta = new JsonObject
                {
                    ["model"] = "recorded",
                    ["choices"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["delta"] = new JsonObject { ["content"] = turn.Text },
                            ["finish_reason"] = null,
                        },
                    },
                };
                builder.Append(Frame(delta.ToJsonString()));
            }

            if (turn.ToolCalls.Count > 0)
            {
                var calls = new JsonArray();
                for (var i = 0; i < turn.ToolCalls.Count; i++)
                    calls.Add(new JsonObject
                    {
                        ["index"] = i,
                        ["id"] = $"call_{i}",
                        ["function"] = new JsonObject
                        {
                            ["name"] = turn.ToolCalls[i].Name,
                            ["arguments"] = turn.ToolCalls[i].Arguments,
                        },
                    });

                var delta = new JsonObject
                {
                    ["model"] = "recorded",
                    ["choices"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["delta"] = new JsonObject { ["tool_calls"] = calls },
                            ["finish_reason"] = null,
                        },
                    },
                };
                builder.Append(Frame(delta.ToJsonString()));
            }

            var finish = turn.ToolCalls.Count > 0 ? "tool_calls" : "stop";
            builder.Append(Frame(
                "{\"model\":\"recorded\",\"choices\":[{\"delta\":{},\"finish_reason\":\""
                + finish
                + "\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}"));
            builder.Append("data: [DONE]\n\n");
            return builder.ToString();
        }

        private static string Frame(string json) => "data: " + json + "\n\n";

        private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Emit(string body)
        {
            await Task.CompletedTask;
            yield return Encoding.UTF8.GetBytes(body);
        }
    }

    /// <summary>
    /// Stands in for every tool the recorded run used, recording what it was
    /// asked for. It answers everything, because the replay is comparing which
    /// calls the loop makes, not what the tools return.
    /// </summary>
    private sealed class ObservingToolset
    {
        public List<RecordedToolCall> Calls { get; } = [];

        public IReadOnlyList<IAgentTool> Tools =>
            [.. PortedTools.Select(name => new ObservingTool(name, Calls))];

        private sealed class ObservingTool(string name, List<RecordedToolCall> calls) : IAgentTool
        {
            public ToolDefinition Definition { get; } = new(
                name, $"recorded tool {name}",
                JsonNode.Parse("""{"type":"object"}""")!);

            public Task<ToolResult> InvokeAsync(
                JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
            {
                calls.Add(new RecordedToolCall(name, Canonical(arguments)));
                return Task.FromResult(ToolResult.Ok("(replayed)"));
            }

            private static string Canonical(JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Object) return element.GetRawText();

                var sorted = new JsonObject();
                foreach (var property in element.EnumerateObject()
                    .OrderBy(p => p.Name, StringComparer.Ordinal))
                    sorted[property.Name] = JsonNode.Parse(property.Value.GetRawText());

                return sorted.ToJsonString();
            }
        }
    }
}
