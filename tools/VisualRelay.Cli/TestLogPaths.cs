namespace VisualRelay.Cli;

/// <summary>
/// Host/VM-safe log destination for a test run. The stem embeds a timestamp, a
/// short hostname, and the PID so concurrent runs from the host and the VM —
/// which share the working folder — never collide on log/TRX filenames. Ported
/// from <c>test.sh</c>'s STEM computation.
/// </summary>
public sealed record TestLogPaths(string Stem, string LogFile, string TrxFile)
{
    public static TestLogPaths Create(string logDir, DateTime timestamp, string host, int pid)
    {
        var safeHost = Sanitize(string.IsNullOrWhiteSpace(host) ? "unknown" : host);
        var stem = $"{timestamp:yyyyMMddTHHmmss}_{safeHost}_{pid}";
        return new TestLogPaths(
            stem,
            Path.Combine(logDir, stem + ".log"),
            Path.Combine(logDir, stem + ".trx"));
    }

    private static string Sanitize(string token)
    {
        var chars = token.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-');
        return new string(chars.ToArray());
    }
}
