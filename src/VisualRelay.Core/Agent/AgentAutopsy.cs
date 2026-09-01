namespace VisualRelay.Core.Agent;

/// <summary>
/// Writes the autopsy artifact for a killed stage: what the model had produced
/// when the watchdog cut it off.
/// <para>
/// The file name and header shape are kept from the subprocess runner's version
/// so anything reading the archived corpus still reads these.
/// </para>
/// </summary>
public static class AgentAutopsy
{
    /// <summary>
    /// Writes the autopsy beside the stage's report.
    /// </summary>
    /// <param name="reportFile">The stage's report path; the sibling is derived from it.</param>
    /// <param name="reason">The kill reason, as it appears on the signature.</param>
    /// <param name="output">What the model produced before the kill.</param>
    /// <returns>The path written, or <c>null</c> when nothing could be written.</returns>
    /// <remarks>
    /// Best-effort throughout. A stage that has already been killed must not
    /// also fail on its own post-mortem.
    /// </remarks>
    public static string? TryWrite(string reportFile, string reason, string output)
    {
        if (string.IsNullOrWhiteSpace(reportFile)) return null;

        try
        {
            var path = Path.ChangeExtension(reportFile, null);
            if (path.EndsWith(".report", StringComparison.Ordinal))
                path = path[..^".report".Length];
            path += ".killed-output.txt";

            var header =
                $"# killed-attempt output (autopsy artifact){Environment.NewLine}" +
                $"# reason: {reason}{Environment.NewLine}" +
                $"# capturedUtc: {DateTimeOffset.UtcNow:O}  bytes: {output.Length}"
                + Environment.NewLine + Environment.NewLine;

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, header + output);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
