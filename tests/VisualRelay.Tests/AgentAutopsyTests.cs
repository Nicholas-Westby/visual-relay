using VisualRelay.Core.Agent;

namespace VisualRelay.Tests;

/// <summary>
/// Covers the autopsy artifact a killed stage leaves behind, and the buffer
/// that supplies it.
/// <para>
/// The subprocess runner had the child's captured stdout to write into this
/// file. In process a cancelled loop returns nothing, so without a buffer of
/// its own a killed stage would leave no record of how far it got — which is
/// exactly the evidence a kill most needs.
/// </para>
/// </summary>
public sealed class AgentAutopsyTests
{
    private static AgentEvent Output(string text) =>
        new(AgentEventKind.TokenDelta, DateTimeOffset.UtcNow, 1, Text: text);

    /// <summary>Model output accumulates in order.</summary>
    [Fact]
    public void TheBuffer_KeepsModelOutputInOrder()
    {
        var buffer = new AgentTranscriptBuffer();

        buffer.Publish(Output("one "));
        buffer.Publish(Output("two"));

        Assert.Equal("one two", buffer.Text());
    }

    /// <summary>
    /// Only model output is kept. A tool result proves the loop is alive but
    /// says nothing about what the model was producing.
    /// </summary>
    [Fact]
    public void TheBuffer_IgnoresEventsThatAreNotModelOutput()
    {
        var buffer = new AgentTranscriptBuffer();

        buffer.Publish(new AgentEvent(
            AgentEventKind.ToolCallFinished, DateTimeOffset.UtcNow, 1, Text: "tool said this"));

        Assert.Equal(string.Empty, buffer.Text());
    }

    /// <summary>
    /// The buffer is bounded and keeps the END. Where a stalled turn got to is
    /// the last thing it wrote, not the first.
    /// </summary>
    [Fact]
    public void TheBuffer_IsBoundedAndKeepsTheEnd()
    {
        var buffer = new AgentTranscriptBuffer(capacity: 8);

        buffer.Publish(Output("abcdefghij"));

        Assert.Equal("cdefghij", buffer.Text());
    }

    /// <summary>The artifact lands beside the report, carrying reason and output.</summary>
    [Fact]
    public void TheAutopsy_LandsBesideTheReportWithItsReason()
    {
        using var repo = TestRepository.Create();
        var reportFile = Path.Combine(repo.Root, "stage3-attempt1.report.json");

        var path = AgentAutopsy.TryWrite(reportFile, "absolute_ceiling", "as far as it got");

        Assert.NotNull(path);
        Assert.Equal(
            Path.Combine(repo.Root, "stage3-attempt1.killed-output.txt"), path);
        var written = File.ReadAllText(path!);
        Assert.Contains("# reason: absolute_ceiling", written, StringComparison.Ordinal);
        Assert.Contains("as far as it got", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stage with no report path writes nothing rather than throwing. A stage
    /// already killed must not also fail on its own post-mortem.
    /// </summary>
    [Fact]
    public void TheAutopsy_WritesNothingWithoutAReportPath()
    {
        Assert.Null(AgentAutopsy.TryWrite(string.Empty, "stall", "output"));
    }
}
