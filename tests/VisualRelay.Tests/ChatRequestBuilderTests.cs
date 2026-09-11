using System.Text.Json;
using System.Text.Json.Nodes;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Covers request shaping: the parameters that must always be sent, the ones
/// that must only be sent where the provider accepts them, and the two shapes
/// that silently corrupted results before — a flattened image list and a
/// <c>developer</c> role.
/// </summary>
public sealed class ChatRequestBuilderTests
{
    private static readonly ProviderCapabilities DeepSeek = ProviderCapabilityCatalog.For("DeepSeek");
    private static readonly ProviderCapabilities Zai = ProviderCapabilityCatalog.For("Z.AI");
    private static readonly ProviderCapabilities Moonshot = ProviderCapabilityCatalog.For("Moonshot");

    private static JsonElement Build(
        IReadOnlyList<ChatMessage> messages,
        ChatRequestOptions options,
        ProviderCapabilities capabilities,
        IReadOnlyList<JsonNode>? tools = null) =>
        JsonDocument.Parse(ChatRequestBuilder.Build(messages, options, capabilities, tools))
            .RootElement.Clone();

    private static JsonNode Tool() => JsonNode.Parse(
        """{"type":"function","function":{"name":"add","parameters":{"type":"object"}}}""")!;

    /// <summary>
    /// A streaming request must carry <c>stream_options.include_usage</c>, or the
    /// provider reports no usage and cost has to be invented.
    /// </summary>
    [Fact]
    public void StreamingRequest_AsksForUsage()
    {
        var body = Build([new ChatMessage("user", "hi")],
            new ChatRequestOptions("deepseek-flash", Stream: true), DeepSeek);

        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    /// <summary>A non-streaming request sends neither key.</summary>
    [Fact]
    public void NonStreamingRequest_OmitsStreamKeys()
    {
        var body = Build([new ChatMessage("user", "hi")],
            new ChatRequestOptions("deepseek-flash"), DeepSeek);

        Assert.False(body.TryGetProperty("stream", out _));
        Assert.False(body.TryGetProperty("stream_options", out _));
    }

    /// <summary>
    /// The <c>developer</c> role is mapped to <c>system</c> unconditionally:
    /// three providers 400 on it and the fourth hangs with zero bytes.
    /// </summary>
    [Fact]
    public void DeveloperRole_IsMappedToSystem()
    {
        var body = Build([new ChatMessage("developer", "be terse")],
            new ChatRequestOptions("deepseek-flash"), DeepSeek);

        Assert.Equal("system", body.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    /// <summary>
    /// A multimodal message keeps its parts array. Flattening it to text is how
    /// images were dropped: the model then answers about nothing, confidently.
    /// </summary>
    [Fact]
    public void MultimodalMessage_KeepsItsPartsArray()
    {
        var message = new ChatMessage("user", Parts:
        [
            ContentPart.FromText("Describe the image."),
            ContentPart.FromImage("data:image/png;base64,AAAA"),
        ]);

        var content = Build([message], new ChatRequestOptions("deepseek-flash"), DeepSeek)
            .GetProperty("messages")[0].GetProperty("content");

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,AAAA",
            content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    /// <summary>A plain text message sends content as a string, not an array.</summary>
    [Fact]
    public void TextMessage_SendsContentAsAString()
    {
        var content = Build([new ChatMessage("user", "hi")],
            new ChatRequestOptions("deepseek-flash"), DeepSeek)
            .GetProperty("messages")[0].GetProperty("content");

        Assert.Equal(JsonValueKind.String, content.ValueKind);
    }

    /// <summary>
    /// Reasoning content is replayed verbatim on an assistant turn bearing tool
    /// calls. Omission is not assumed safe: a probe recorded a hard 400 without it.
    /// </summary>
    [Fact]
    public void ReasoningContent_IsCarriedForwardOnToolCallTurns()
    {
        var assistant = new ChatMessage("assistant",
            ReasoningContent: "the user wants a sum",
            ToolCalls: [new ToolCall("call_1", "add", """{"a":2,"b":2}""")]);

        var message = Build([assistant], new ChatRequestOptions("deepseek-flash"), DeepSeek)
            .GetProperty("messages")[0];

        Assert.Equal("the user wants a sum", message.GetProperty("reasoning_content").GetString());
        var call = message.GetProperty("tool_calls")[0];
        Assert.Equal("call_1", call.GetProperty("id").GetString());
        Assert.Equal("add", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"a":2,"b":2}""",
            call.GetProperty("function").GetProperty("arguments").GetString());
    }

    /// <summary>A message with no reasoning trace does not send an empty one.</summary>
    [Fact]
    public void MessageWithoutReasoning_OmitsTheField()
    {
        var message = Build([new ChatMessage("assistant", "done")],
            new ChatRequestOptions("deepseek-flash"), DeepSeek).GetProperty("messages")[0];

        Assert.False(message.TryGetProperty("reasoning_content", out _));
    }

    /// <summary>
    /// Z.AI accepts required tool choice while thinking, so the parameter is
    /// sent. It is the only one of the four that does, confirmed live against
    /// the goldened body on 2026-09-01.
    /// </summary>
    [Fact]
    public void RequiredToolChoice_IsSentWhereAccepted()
    {
        var body = Build([new ChatMessage("user", "add 2 and 2")],
            new ChatRequestOptions("glm-5.3-flash", RequireToolCall: true), Zai, [Tool()]);

        Assert.Equal("required", body.GetProperty("tool_choice").GetString());
    }

    /// <summary>
    /// DeepSeek rejects it too, which was recorded the other way round. Its own
    /// 400 reads "Thinking mode does not support this tool_choice", and thinking
    /// is V4's default — so a probe with reasoning off would have said the
    /// opposite.
    /// </summary>
    [Fact]
    public void RequiredToolChoice_IsWithheldFromDeepSeekToo()
    {
        var body = Build([new ChatMessage("user", "add 2 and 2")],
            new ChatRequestOptions("deepseek-flash", RequireToolCall: true), DeepSeek, [Tool()]);

        Assert.False(body.TryGetProperty("tool_choice", out _));
    }

    /// <summary>
    /// Moonshot returns 400 for required tool choice while thinking is on, so the
    /// parameter is withheld and the caller coerces through the prompt instead.
    /// </summary>
    [Fact]
    public void RequiredToolChoice_IsWithheldWhereRejected()
    {
        var body = Build([new ChatMessage("user", "add 2 and 2")],
            new ChatRequestOptions("kimi-k2", RequireToolCall: true), Moonshot, [Tool()]);

        Assert.False(body.TryGetProperty("tool_choice", out _));
        Assert.True(Moonshot.NeedsPromptLevelToolCoercion);
    }

    /// <summary>
    /// An effort value the provider does not accept is withheld rather than sent
    /// and ignored: Z.AI answers 400 code 1210 for one outside its set.
    /// </summary>
    [Fact]
    public void UnsupportedReasoningEffort_IsWithheld()
    {
        var body = Build([new ChatMessage("user", "hi")],
            new ChatRequestOptions("glm-5.3-flash", ReasoningEffort: "none"), Zai);

        Assert.False(body.TryGetProperty("reasoning_effort", out _));
    }

    /// <summary>An effort value the provider accepts is sent through.</summary>
    [Fact]
    public void SupportedReasoningEffort_IsSent()
    {
        var zai = Build([new ChatMessage("user", "hi")],
            new ChatRequestOptions("glm-5.3-flash", ReasoningEffort: "low"), Zai);
        var deepseek = Build([new ChatMessage("user", "hi")],
            new ChatRequestOptions("deepseek-flash", ReasoningEffort: "none"), DeepSeek);

        Assert.Equal("low", zai.GetProperty("reasoning_effort").GetString());
        Assert.Equal("none", deepseek.GetProperty("reasoning_effort").GetString());
    }

    /// <summary>
    /// No output ceiling is sent unless one is asked for, so a reasoning model is
    /// never capped low enough to spend its whole budget before writing content.
    /// </summary>
    [Fact]
    public void NoMaxTokens_IsSentByDefault()
    {
        var body = Build([new ChatMessage("user", "hi")],
            new ChatRequestOptions("glm-5.3-flash"), Zai);

        Assert.False(body.TryGetProperty("max_tokens", out _));
    }

    /// <summary>A tool result message carries the id of the call it answers.</summary>
    [Fact]
    public void ToolResultMessage_CarriesItsCallId()
    {
        var message = Build([new ChatMessage("tool", "4", ToolCallId: "call_1")],
            new ChatRequestOptions("deepseek-flash"), DeepSeek).GetProperty("messages")[0];

        Assert.Equal("tool", message.GetProperty("role").GetString());
        Assert.Equal("call_1", message.GetProperty("tool_call_id").GetString());
    }

    /// <summary>An unknown provider gets the conservative capability set.</summary>
    [Fact]
    public void UnknownProvider_GetsConservativeCapabilities()
    {
        var capabilities = ProviderCapabilityCatalog.For("Some New Host");

        Assert.False(capabilities.CanDisableReasoning);
        Assert.False(capabilities.SupportsRequiredToolChoiceWhileThinking);
        Assert.Empty(capabilities.ReasoningEffortValues);
    }
}
