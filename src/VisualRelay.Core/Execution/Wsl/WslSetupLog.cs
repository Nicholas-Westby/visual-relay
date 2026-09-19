using System.Diagnostics;
using System.Globalization;

namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// Everything one <c>setup-wsl</c> run asked wsl.exe to do, and everything it answered: the
/// command, its exit code, the time it took and its whole output. The console shows only the
/// end of a failed step's output, and a step that exits 0 can still say what went wrong: on
/// 2026-09-19 WSL's "restart needed" notice came from a step that exited 0, and setup threw it
/// away. The first call of a run replaces the previous run's log, and each call is added as it
/// finishes, so a run that is stopped keeps what it did. Writing is best effort: a log that
/// cannot be written never stops the setup.
/// </summary>
public sealed class WslSetupLog(string path)
{
    private readonly Lock _gate = new();
    private bool _started;

    public string Path => path;

    /// <summary>True once this run has written a call to the log, so there is something to point at.</summary>
    public bool HasEntries
    {
        get { lock (_gate) { return _started; } }
    }

    /// <summary>The log on this machine: <c>%LOCALAPPDATA%\visual-relay\setup-wsl.log</c> on Windows.</summary>
    public static WslSetupLog ThisMachine() => new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "visual-relay", "setup-wsl.log"));

    /// <summary><paramref name="run"/>, with every call it makes recorded here under <paramref name="how"/>.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> Recording(
        Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> run, string how) =>
        async (argv, ct) =>
        {
            var started = Stopwatch.GetTimestamp();
            var result = await run(argv, ct);
            Record(how, argv, result.ExitCode, Stopwatch.GetElapsedTime(started), result.Output);
            return result;
        };

    private void Record(string how, IReadOnlyList<string> argv, int exitCode, TimeSpan took, string output)
    {
        var text = output.Replace("\r\n", "\n");
        var ending = text.Length == 0 || text.EndsWith('\n') ? "" : "\n";
        var entry = string.Create(CultureInfo.InvariantCulture,
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {how} {string.Join(' ', argv.Select(Quote))}\nexit {exitCode} after {took.TotalSeconds:0.0} s\n{text}{ending}\n");
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                if (_started)
                    File.AppendAllText(path, entry);
                else
                    File.WriteAllText(path, entry);
                _started = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is there to diagnose a setup, never to stop one.
        }
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";
}
