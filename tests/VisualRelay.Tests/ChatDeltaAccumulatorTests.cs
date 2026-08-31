using System.Text.Json;
using VisualRelay.Core.Llm;

namespace VisualRelay.Tests;

/// <summary>
/// Covers <see cref="ChatDeltaAccumulator"/> against the two opposite tool-call
/// streaming styles measured on the providers: Hugging Face repeating an id with
/// an empty function object, and Z.AI delivering a whole call in one delta.
/// Keying on id breaks the first; assuming empty first arguments breaks the second.
/// </summary>
public sealed class ChatDeltaAccumulatorTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ChatDeltaAccumulator Feed(params string[] chunks)
    {
        var accumulator = new ChatDeltaAccumulator();
        foreach (var chunk in chunks) accumulator.Append(Parse(chunk));
        return accumulator;
    }

    /// <summary>Content deltas concatenate in arrival order.</summary>
    [Fact]
    public void ContentDeltas_Concatenate()
    {
        var accumulator = Feed(
            """{"choices":[{"delta":{"content":"Hel"}}]}""",
            """{"choices":[{"delta":{"content":"lo"}}]}""");

        Assert.Equal("Hello", accumulator.Content());
        Assert.True(accumulator.SawOutput);
    }

    /// <summary>Append returns just the text this chunk added, for live streaming.</summary>
    [Fact]
    public void Append_ReturnsOnlyTheNewText()
    {
        var accumulator = new ChatDeltaAccumulator();

        Assert.Equal("Hel", accumulator.Append(Parse("""{"choices":[{"delta":{"content":"Hel"}}]}""")));
        Assert.Equal("lo", accumulator.Append(Parse("""{"choices":[{"delta":{"content":"lo"}}]}""")));
    }

    /// <summary>Reasoning deltas accumulate separately from content.</summary>
    [Fact]
    public void ReasoningDeltas_AccumulateSeparately()
    {
        var accumulator = Feed(
            """{"choices":[{"delta":{"reasoning_content":"think"}}]}""",
            """{"choices":[{"delta":{"content":"answer"}}]}""");

        Assert.Equal("answer", accumulator.Content());
        Assert.Equal("think", accumulator.ReasoningContent());
    }

    /// <summary>With no reasoning at all the trace stays null rather than empty.</summary>
    [Fact]
    public void NoReasoning_LeavesTheTraceNull()
    {
        Assert.Null(Feed("""{"choices":[{"delta":{"content":"hi"}}]}""").ReasoningContent());
    }

    /// <summary>A tool call assembled across deltas joins into one call.</summary>
    [Fact]
    public void ToolCallFragments_AssembleByIndex()
    {
        var accumulator = Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"add","arguments":"{\"a\""}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":":2,\"b\":2}"}}]}}]}""");

        var call = Assert.Single(accumulator.ToolCalls());
        Assert.Equal("call_1", call.Id);
        Assert.Equal("add", call.Name);
        Assert.Equal("""{"a":2,"b":2}""", call.Arguments);
    }

    /// <summary>
    /// Hugging Face repeats the same id in a later delta with an empty function
    /// object. Keyed on id that invents a second, phantom call; keyed on index it
    /// stays one.
    /// </summary>
    [Fact]
    public void RepeatedIdWithEmptyFunction_DoesNotInventAPhantomCall()
    {
        var accumulator = Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"add","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{}}]}}]}""");

        var call = Assert.Single(accumulator.ToolCalls());
        Assert.Equal("add", call.Name);
        Assert.Equal("{}", call.Arguments);
    }

    /// <summary>
    /// Z.AI sends a complete tool call in a single delta, so nothing may assume
    /// the first fragment carries empty arguments.
    /// </summary>
    [Fact]
    public void WholeToolCallInOneDelta_IsAccepted()
    {
        var accumulator = Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"add","arguments":"{\"a\":1,\"b\":2}"}}]}}]}""");

        var call = Assert.Single(accumulator.ToolCalls());
        Assert.Equal("""{"a":1,"b":2}""", call.Arguments);
    }

    /// <summary>Two parallel calls stay separate and come back in index order.</summary>
    [Fact]
    public void ParallelToolCalls_StaySeparateAndOrdered()
    {
        var accumulator = Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"b","function":{"name":"second","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"a","function":{"name":"first","arguments":"{}"}}]}}]}""");

        var calls = accumulator.ToolCalls();

        Assert.Equal(2, calls.Count);
        Assert.Equal("first", calls[0].Name);
        Assert.Equal("second", calls[1].Name);
    }

    /// <summary>A fragment that never carried a name is a phantom and is dropped.</summary>
    [Fact]
    public void NamelessToolCall_IsDropped()
    {
        var accumulator = Feed(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{}}]}}]}""");

        Assert.Empty(accumulator.ToolCalls());
    }

    /// <summary>The finish reason and the served model are captured.</summary>
    [Fact]
    public void FinishReasonAndServedModel_AreCaptured()
    {
        var accumulator = Feed(
            """{"model":"deepseek-v4-flash","choices":[{"delta":{"content":"hi"},"finish_reason":null}]}""",
            """{"model":"deepseek-v4-flash","choices":[{"delta":{},"finish_reason":"stop"}]}""");

        Assert.Equal("stop", accumulator.FinishReason);
        Assert.Equal("deepseek-v4-flash", accumulator.ServedModel);
    }

    /// <summary>
    /// A trailing usage chunk with no choices at all still updates usage, which
    /// is the only place Hugging Face ever reports it.
    /// </summary>
    [Fact]
    public void TrailingUsageChunkWithNoChoices_IsRead()
    {
        var accumulator = Feed(
            """{"choices":[{"delta":{"content":"hi"}}]}""",
            """{"choices":[],"usage":{"prompt_tokens":10,"completion_tokens":2}}""");

        Assert.Equal(10, accumulator.Usage!.PromptTokens);
        Assert.Equal("hi", accumulator.Content());
    }

    /// <summary>
    /// Usage reported twice is not doubled: the second report replaces the first.
    /// </summary>
    [Fact]
    public void UsageReportedTwice_IsNotDoubled()
    {
        var accumulator = Feed(
            """{"choices":[{"delta":{},"finish_reason":"stop","usage":{"prompt_tokens":11,"completion_tokens":4}}]}""",
            """{"choices":[],"usage":{"prompt_tokens":11,"completion_tokens":4}}""");

        Assert.Equal(11, accumulator.Usage!.PromptTokens);
        Assert.Equal(4, accumulator.Usage.CompletionTokens);
    }

    /// <summary>
    /// The GLM shape: a finish reason of length with no content at all. The
    /// caller must be able to tell this from a genuine empty answer.
    /// </summary>
    [Fact]
    public void EmptyContentWithLengthFinish_IsDistinguishable()
    {
        var accumulator = Feed(
            """{"choices":[{"delta":{"reasoning_content":"thinking"},"finish_reason":null}]}""",
            """{"choices":[{"delta":{},"finish_reason":"length"}]}""");

        Assert.Equal(string.Empty, accumulator.Content());
        Assert.Equal("length", accumulator.FinishReason);
        Assert.NotNull(accumulator.ReasoningContent());
    }

    /// <summary>A chunk that is not an object is ignored rather than throwing.</summary>
    [Fact]
    public void NonObjectChunk_IsIgnored()
    {
        var accumulator = new ChatDeltaAccumulator();

        Assert.Equal(string.Empty, accumulator.Append(Parse("[]")));
        Assert.False(accumulator.SawOutput);
    }
}
