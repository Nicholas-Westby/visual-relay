using System.Text.Json;
using System.Text.Json.Nodes;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>
/// A tool as the model sees it: the name it calls, the sentence it reads, and the
/// JSON schema its arguments are validated against.
/// </summary>
/// <param name="Name">
/// The wire name. <c>view_image</c> and <c>list_files</c> are load-bearing: stage
/// prompts name them as literal strings, so renaming either breaks a prompt.
/// </param>
/// <param name="Description">What the tool does, in one or two sentences.</param>
/// <param name="ParametersSchema">JSON Schema for the arguments object.</param>
public sealed record ToolDefinition(string Name, string Description, JsonNode ParametersSchema);

/// <summary>What a tool hands back to the loop, and through it to the model.</summary>
/// <param name="Content">The text the model sees.</param>
/// <param name="IsError">
/// True when the call failed. The loop counts these for the consecutive-error
/// guardrail, and the model still sees <paramref name="Content"/> so it can adapt.
/// </param>
/// <param name="Images">
/// Image content the call produced, as <c>data:image/...;base64,</c> URIs; null
/// for the text-only tools, which is all of them but <c>view_image</c>. It is a
/// field rather than something smuggled inside <paramref name="Content"/>
/// because the tool-result message itself stays text on the wire — the
/// <c>tool</c> role carries no image parts on these OpenAI-compatible providers
/// — so the loop turns each URI into a <c>ContentPart.FromImage</c> on a
/// following user message. Keeping the blob out of the text also means the
/// compaction ladder can drop an old screenshot without shredding the words
/// beside it, and no base64 is ever counted or truncated as prose.
/// </param>
public sealed record ToolResult(string Content, bool IsError = false, IReadOnlyList<string>? Images = null)
{
    /// <summary>A successful result.</summary>
    /// <param name="content">The text the model sees.</param>
    /// <returns>The result.</returns>
    public static ToolResult Ok(string content) => new(content);

    /// <summary>A failed result. The message must tell the model how to adapt.</summary>
    /// <param name="message">What went wrong, in terms the model can act on.</param>
    /// <returns>The result.</returns>
    public static ToolResult Error(string message) => new(message, IsError: true);

    /// <summary>A successful result carrying image content alongside its text.</summary>
    /// <param name="content">The text the model sees, describing what was attached.</param>
    /// <param name="images">One or more <c>data:image/...;base64,</c> URIs.</param>
    /// <returns>The result.</returns>
    public static ToolResult WithImages(string content, IReadOnlyList<string> images) =>
        new(content, Images: images);
}

/// <summary>
/// Everything a tool needs from the run it belongs to. Passed per invocation
/// rather than captured, so a tool instance is stateless and shareable.
/// </summary>
/// <param name="TargetRoot">Absolute path to the repository under work.</param>
/// <param name="RemainingStageBudget">
/// What is left of the stage's wall clock. This is the ONLY ceiling on a tool
/// invocation: there is no hidden per-call cap. A model-requested timeout is
/// honoured up to this, and if it is ever reduced the tool result says so, so the
/// model can adapt instead of retrying the same doomed number.
/// </param>
public sealed record ToolContext(string TargetRoot, TimeSpan RemainingStageBudget);

/// <summary>One tool the model can call.</summary>
public interface IAgentTool
{
    /// <summary>How this tool is advertised to the model.</summary>
    ToolDefinition Definition { get; }

    /// <summary>
    /// Runs the tool. Must not throw for an ordinary failure: return
    /// <see cref="ToolResult.Error"/> so the model can read what went wrong.
    /// </summary>
    /// <param name="arguments">The model's arguments, already parsed.</param>
    /// <param name="context">The run this call belongs to.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>What the model sees.</returns>
    Task<ToolResult> InvokeAsync(
        JsonElement arguments, ToolContext context, CancellationToken cancellationToken);
}
