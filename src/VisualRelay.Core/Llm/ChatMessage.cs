namespace VisualRelay.Core.Llm;

/// <summary>One part of a multimodal message body.</summary>
/// <param name="Type">Either <c>text</c> or <c>image_url</c>.</param>
/// <param name="Text">The text, for a text part.</param>
/// <param name="ImageUrl">The URL or <c>data:</c> URI, for an image part.</param>
public sealed record ContentPart(string Type, string? Text = null, string? ImageUrl = null)
{
    /// <summary>Builds a text part.</summary>
    /// <param name="text">The text content.</param>
    /// <returns>The part.</returns>
    public static ContentPart FromText(string text) => new("text", Text: text);

    /// <summary>Builds an image part.</summary>
    /// <param name="url">An https URL or a <c>data:image/...;base64,</c> URI.</param>
    /// <returns>The part.</returns>
    public static ContentPart FromImage(string url) => new("image_url", ImageUrl: url);
}

/// <summary>A tool call the model asked for.</summary>
/// <param name="Id">The provider's id for this call, echoed back on the result.</param>
/// <param name="Name">The tool name.</param>
/// <param name="Arguments">The raw JSON argument string, exactly as the model produced it.</param>
public sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>
/// One message in a conversation. Content is either a plain string or a list of
/// parts; the two are kept separate so a multimodal message is never flattened
/// to text, which is precisely how images used to be dropped silently.
/// </summary>
/// <param name="Role">
/// <c>system</c>, <c>user</c>, <c>assistant</c> or <c>tool</c>. A
/// <c>developer</c> role is mapped to <c>system</c> when the body is built:
/// three of the four providers 400 on it and the fourth hangs.
/// </param>
/// <param name="Content">Plain text content, when the message is not multimodal.</param>
/// <param name="Parts">Multimodal parts. Takes precedence over <paramref name="Content"/>.</param>
/// <param name="ReasoningContent">
/// The provider's reasoning trace for an assistant turn. Replayed verbatim on
/// assistant messages bearing tool calls: it is free to send, quality-relevant
/// on two providers, and at least one probe recorded a hard 400 without it.
/// </param>
/// <param name="ToolCalls">Tool calls this assistant message is asking for.</param>
/// <param name="ToolCallId">The call this tool-result message answers.</param>
public sealed record ChatMessage(
    string Role,
    string? Content = null,
    IReadOnlyList<ContentPart>? Parts = null,
    string? ReasoningContent = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolCallId = null)
{
    /// <summary>True when this message carries image or multi-part content.</summary>
    public bool IsMultimodal => Parts is { Count: > 0 };
}
