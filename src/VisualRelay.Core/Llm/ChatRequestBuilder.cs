using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Llm;

/// <summary>
/// Options for one chat completion request.
/// </summary>
/// <param name="Model">The concrete model name to send.</param>
/// <param name="Stream">Whether to request a streamed response.</param>
/// <param name="MaxTokens">
/// Output ceiling. Leave null on a reasoning model: reasoning is billed inside
/// the completion count and is emitted BEFORE content, so a low cap returns
/// empty content with <c>finish_reason: "length"</c> rather than a short answer.
/// </param>
/// <param name="Temperature">Sampling temperature, when the caller pins one.</param>
/// <param name="ReasoningEffort">
/// Effort level. Only sent when the provider accepts the value; an unsupported
/// value is a 400 on Z.AI rather than being ignored.
/// </param>
/// <param name="RequireToolCall">
/// Whether the model must call a tool. Sent as <c>tool_choice: "required"</c>
/// only where that is accepted; elsewhere the caller coerces via the prompt.
/// </param>
public sealed record ChatRequestOptions(
    string Model,
    bool Stream = false,
    int? MaxTokens = null,
    double? Temperature = null,
    string? ReasoningEffort = null,
    bool RequireToolCall = false);

/// <summary>
/// Builds the JSON body for an OpenAI-compatible <c>/chat/completions</c> call,
/// shaped for the provider that will serve it.
/// </summary>
public static class ChatRequestBuilder
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>
    /// Serializes a request body.
    /// </summary>
    /// <param name="messages">The conversation, oldest first.</param>
    /// <param name="options">Request options.</param>
    /// <param name="capabilities">The serving provider's measured capabilities.</param>
    /// <param name="tools">Tool definitions, already in JSON schema form.</param>
    /// <returns>The serialized JSON body.</returns>
    public static string Build(
        IReadOnlyList<ChatMessage> messages,
        ChatRequestOptions options,
        ProviderCapabilities capabilities,
        IReadOnlyList<JsonNode>? tools = null)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["messages"] = BuildMessages(messages),
        };

        if (options.Stream)
        {
            body["stream"] = true;
            // Without this the streaming path reports no usage at all, which is
            // why costs used to be fabricated from answer length.
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        if (options.MaxTokens is { } maxTokens) body["max_tokens"] = maxTokens;
        if (options.Temperature is { } temperature) body["temperature"] = temperature;

        // Only send an effort the provider actually accepts. Z.AI 400s on a value
        // outside its set rather than ignoring it.
        if (options.ReasoningEffort is { Length: > 0 } effort
            && capabilities.ReasoningEffortValues.Contains(effort, StringComparer.OrdinalIgnoreCase))
            body["reasoning_effort"] = effort;

        if (tools is { Count: > 0 })
        {
            var array = new JsonArray();
            foreach (var tool in tools) array.Add(tool.DeepClone());
            body["tools"] = array;

            if (options.RequireToolCall && capabilities.SupportsRequiredToolChoiceWhileThinking)
                body["tool_choice"] = "required";
        }

        return body.ToJsonString(Compact);
    }

    private static JsonArray BuildMessages(IReadOnlyList<ChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages) array.Add(BuildMessage(message));
        return array;
    }

    private static JsonObject BuildMessage(ChatMessage message)
    {
        var json = new JsonObject { ["role"] = NormalizeRole(message.Role) };

        if (message.IsMultimodal)
        {
            // Never flattened to text: flattening is exactly how images were
            // being dropped, at which point the model answers about nothing.
            var parts = new JsonArray();
            foreach (var part in message.Parts!) parts.Add(BuildPart(part));
            json["content"] = parts;
        }
        else
        {
            json["content"] = message.Content ?? string.Empty;
        }

        // Replayed verbatim on assistant turns bearing tool calls. Free to send,
        // quality-relevant on two providers, and one probe recorded a hard 400
        // when it was absent, so omission is not assumed to be safe.
        if (message.ReasoningContent is { Length: > 0 } reasoning)
            json["reasoning_content"] = reasoning;

        if (message.ToolCalls is { Count: > 0 } calls)
        {
            var array = new JsonArray();
            foreach (var call in calls)
                array.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = call.Name,
                        ["arguments"] = call.Arguments,
                    },
                });
            json["tool_calls"] = array;
        }

        if (message.ToolCallId is { Length: > 0 } toolCallId)
            json["tool_call_id"] = toolCallId;

        return json;
    }

    private static JsonObject BuildPart(ContentPart part) => part.Type switch
    {
        "image_url" => new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = part.ImageUrl ?? string.Empty },
        },
        _ => new JsonObject { ["type"] = "text", ["text"] = part.Text ?? string.Empty },
    };

    /// <summary>
    /// Maps <c>developer</c> to <c>system</c> unconditionally: three of the four
    /// providers return 400 on that role and the fourth hangs indefinitely with
    /// zero bytes, which is the real cause of the byte-0 stall.
    /// </summary>
    private static string NormalizeRole(string role) =>
        role.Equals("developer", StringComparison.OrdinalIgnoreCase) ? "system" : role;
}
