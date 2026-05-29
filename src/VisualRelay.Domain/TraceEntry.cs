namespace VisualRelay.Domain;

public enum TraceEntryKind
{
    AssistantText,
    ToolCall,
    ToolResult,
    UserText
}

public sealed record TraceEntry(TraceEntryKind Kind, string Title, string Content);

