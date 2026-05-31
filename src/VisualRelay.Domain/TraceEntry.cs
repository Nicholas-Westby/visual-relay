namespace VisualRelay.Domain;

public enum TraceEntryKind
{
    AssistantText,
    Thinking,
    ToolCall,
    ToolResult,
    UserText
}

public sealed record TraceEntry(
    TraceEntryKind Kind,
    string Title,
    string Content,
    int? StageNumber = null)
{
    public string ScopeLabel => StageNumber is null ? "task" : $"s{StageNumber:00}";
}
