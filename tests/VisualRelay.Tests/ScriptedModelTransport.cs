using System.Text;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// A fake model scripted at TURN granularity: each call to the transport serves
/// the next canned response in the queue.
/// <para>
/// The existing stage-level doubles script one whole canned contract per stage
/// and never reach inside a turn, so nothing today can exercise what the loop
/// does when a model asks for a tool that does not exist, repeats itself, or
/// sends damaged arguments. This can.
/// </para>
/// <para>
/// Every chunk is delivered up front, so the loop's behaviour is exercised
/// without involving the clock. Stream timing lives in
/// <see cref="ChatCompletionClientTests"/>, which is where the budgets belong.
/// </para>
/// </summary>
internal sealed class ScriptedModelTransport : IProviderTransport
{
    private readonly Queue<(int Status, string Body)> _script = new();

    /// <summary>Every request body the loop sent, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Queues a plain text answer with no tool calls.</summary>
    /// <param name="text">What the model says.</param>
    /// <returns>This, for chaining.</returns>
    public ScriptedModelTransport Answer(string text)
    {
        var escaped = System.Text.Json.JsonEncodedText.Encode(text).ToString();
        return Raw(200,
            Sse($$"""{"model":"fake-1","choices":[{"delta":{"content":"{{escaped}}"},"finish_reason":null}]}""")
            + Sse("""{"model":"fake-1","choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":4}}""")
            + "data: [DONE]\n\n");
    }

    /// <summary>Queues a turn asking for one tool call.</summary>
    /// <param name="name">The tool name the model asks for.</param>
    /// <param name="arguments">The raw argument JSON it sends.</param>
    /// <returns>This, for chaining.</returns>
    public ScriptedModelTransport CallsTool(string name, string arguments)
    {
        // Built by concatenation rather than interpolation: the JSON here nests
        // braces four deep, which fights raw-string brace counting for no gain.
        var escapedArgs = System.Text.Json.JsonEncodedText.Encode(arguments).ToString();
        var call = "{\"model\":\"fake-1\",\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,"
            + "\"id\":\"c" + _script.Count + "\",\"function\":{\"name\":\"" + name + "\","
            + "\"arguments\":\"" + escapedArgs + "\"}}]},\"finish_reason\":null}]}";

        return Raw(200,
            Sse(call)
            + Sse("""{"model":"fake-1","choices":[{"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":6}}""")
            + "data: [DONE]\n\n");
    }

    /// <summary>Queues an HTTP failure.</summary>
    /// <param name="status">The status to answer with.</param>
    /// <param name="body">The error body.</param>
    /// <returns>This, for chaining.</returns>
    public ScriptedModelTransport Fails(int status, string body) => Raw(status, body);

    /// <summary>
    /// Queues a turn that spends its whole output budget on reasoning and emits
    /// no content, finishing on length. This is the measured GLM shape.
    /// </summary>
    /// <returns>This, for chaining.</returns>
    public ScriptedModelTransport ReasonsWithoutAnswering() => Raw(200,
        Sse("""{"model":"fake-1","choices":[{"delta":{"reasoning_content":"thinking"},"finish_reason":null}]}""")
        + Sse("""{"model":"fake-1","choices":[{"delta":{},"finish_reason":"length"}],"usage":{"prompt_tokens":10,"completion_tokens":20,"completion_tokens_details":{"reasoning_tokens":20}}}""")
        + "data: [DONE]\n\n");

    private ScriptedModelTransport Raw(int status, string body)
    {
        _script.Enqueue((status, body));
        return this;
    }

    private static string Sse(string json) => "data: " + json + "\n\n";

    /// <inheritdoc />
    public Task<ProviderResponse> SendAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request.Body);
        var (status, body) = Next();
        return Task.FromResult(new ProviderResponse(status, new Dictionary<string, string>(), body));
    }

    /// <inheritdoc />
    public Task<ProviderStreamResponse> StreamAsync(
        ProviderRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request.Body);
        var (status, body) = Next();
        return Task.FromResult(new ProviderStreamResponse(
            status, new Dictionary<string, string>(), _ => Emit(body)));
    }

    private (int Status, string Body) Next() =>
        _script.Count > 0
            ? _script.Dequeue()
            // Running off the end of the script is a test bug, not a model
            // behaviour: say so rather than hanging or answering something.
            : throw new InvalidOperationException(
                "ScriptedModelTransport: the loop asked for more turns than the script provides. "
                + "Queue another response, or assert that the loop stops sooner.");

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Emit(string body)
    {
        await Task.CompletedTask;
        yield return Encoding.UTF8.GetBytes(body);
    }
}
