using VisualRelay.Core.Traces;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

public sealed class TraceParserTests
{
    [Fact]
    public void Parse_DropsPromptRecordsAndKeepsAssistantToolOperations()
    {
        var jsonl = string.Join(
            Environment.NewLine,
            """{"type":"system","content":"hidden"}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"I will inspect."},{"type":"tool_use","name":"shell","input":{"cmd":"ls"}}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"checking assumptions"}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_result","content":"README.md"}]}}""",
            """{"type":"last-prompt","lastPrompt":"hidden"}""",
            "{not json");

        var entries = RelayTraceParser.Parse(jsonl);

        Assert.Equal(
            [TraceEntryKind.AssistantText, TraceEntryKind.ToolCall, TraceEntryKind.Thinking, TraceEntryKind.ToolResult],
            entries.Select(e => e.Kind));
        Assert.Contains(entries, e => e.Title == "shell" && e.Content.Contains("\"cmd\":\"ls\"", StringComparison.Ordinal));
        Assert.Contains(entries, e => e is { Kind: TraceEntryKind.Thinking, Content: "checking assumptions" });
        Assert.Contains(entries, e => e.Content == "README.md");
    }
}
