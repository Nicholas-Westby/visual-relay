using System.Text;

namespace VisualRelay.Core.Agent;

/// <summary>
/// Keeps the tail of what the model produced, so a killed stage can still be
/// read afterwards.
/// <para>
/// The subprocess runner had the child's captured stdout to write into its
/// autopsy file. In process there is no such buffer: a cancelled loop returns
/// nothing, and everything it streamed is gone. This holds the last stretch of
/// it against exactly that case.
/// </para>
/// </summary>
/// <param name="capacity">How many characters of output to keep.</param>
public sealed class AgentTranscriptBuffer(int capacity = AgentTranscriptBuffer.DefaultCapacity)
    : IAgentEventSink
{
    /// <summary>Characters kept by default: enough to read, small enough to hold.</summary>
    internal const int DefaultCapacity = 64 * 1024;

    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Publish(AgentEvent agentEvent)
    {
        if (!agentEvent.IsModelOutput || agentEvent.Text is not { Length: > 0 } text) return;

        lock (_gate)
        {
            _text.Append(text);
            // Trim from the front: the end of a stalled turn is the part that
            // says where it got to.
            if (_text.Length > capacity) _text.Remove(0, _text.Length - capacity);
        }
    }

    /// <summary>What the model produced, up to the capacity.</summary>
    /// <returns>The retained tail of the output.</returns>
    public string Text()
    {
        lock (_gate) return _text.ToString();
    }
}
