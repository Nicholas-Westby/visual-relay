using System.Globalization;
using System.Text;

namespace VisualRelay.Core.Agent.Tools;

/// <summary>Shared formatting for tool output: size rendering and truncation markers.</summary>
internal static class ToolText
{
    /// <summary>Renders a byte count the way a person reads one.</summary>
    /// <param name="count">The number of bytes.</param>
    /// <returns>A short human-readable size.</returns>
    internal static string Bytes(long count) => count switch
    {
        < 1024 => $"{count} B",
        < 1024 * 1024 => $"{count / 1024.0:0.#} KiB",
        _ => $"{count / (1024.0 * 1024.0):0.#} MiB",
    };

    /// <summary>Renders a count with digit grouping, so 12345 reads as 12,345.</summary>
    /// <param name="count">The number.</param>
    /// <returns>The grouped rendering.</returns>
    internal static string Count(int count) => count.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Appends a truncation marker. Every cap in this tool set reports through
    /// here so the model always learns what was dropped and how to ask for less.
    /// </summary>
    /// <param name="body">The output produced so far.</param>
    /// <param name="tool">The tool that truncated.</param>
    /// <param name="what">What was elided, e.g. "1,203 more matching lines".</param>
    /// <param name="howToNarrow">The concrete next call that avoids the cap.</param>
    /// <returns>The body plus the marker.</returns>
    internal static string Marker(string body, string tool, string what, string howToNarrow)
    {
        var builder = new StringBuilder(body);
        if (body.Length > 0 && !body.EndsWith('\n')) builder.Append('\n');
        builder.Append("[").Append(tool).Append(": truncated — ").Append(what)
            .Append(". ").Append(howToNarrow).Append("]\n");
        return builder.ToString();
    }

    /// <summary>
    /// Clips a single line to a character budget, marking it so the model does
    /// not mistake a clipped line for the file's real content.
    /// </summary>
    /// <param name="line">The line.</param>
    /// <param name="maxChars">The budget.</param>
    /// <returns>The line, clipped if it was over budget.</returns>
    internal static string ClipLine(string line, int maxChars) =>
        line.Length <= maxChars
            ? line
            : line[..maxChars] + $" …[+{Count(line.Length - maxChars)} chars]";
}
