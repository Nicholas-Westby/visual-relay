using System.Text;
using System.Text.Json.Nodes;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// The transport half of the replay harness: it serves recorded assistant
/// turns back to the loop as if a provider had streamed them.
/// </summary>
public static partial class TraceReplayHarness
{
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
}
