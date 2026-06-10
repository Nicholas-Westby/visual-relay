using System.ComponentModel;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Runs a test command via direct exec (no /bin/sh -lc wrapper) so exit code 127
/// (command-not-found) is surfaced reliably. Time-boxed by a configurable timeout.
/// </summary>
public sealed class DirectExecTestRunner : ITestRunner
{
    private readonly TimeSpan _timeout;

    public DirectExecTestRunner(TimeSpan? timeout = null) =>
        _timeout = timeout ?? TimeSpan.FromSeconds(5);

    public async Task<TestRunResult> RunAsync(
        string rootPath,
        string command,
        CancellationToken cancellationToken = default)
    {
        // Split on whitespace: first token is the executable, remainder are args.
        var parts = SplitCommand(command);
        if (parts.Count == 0)
        {
            return new TestRunResult(127, string.Empty, false);
        }

        try
        {
            var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
                parts[0],
                parts.Skip(1),
                rootPath,
                _timeout,
                cancellationToken);

            return new TestRunResult(exitCode, output, timedOut);
        }
        catch (Win32Exception)
        {
            // ENOENT — file not found. Map to exit 127 (shell convention for
            // command-not-found) with no output.
            return new TestRunResult(127, string.Empty, false);
        }
    }

    /// <summary>
    /// Splits a command string on whitespace, respecting simple quoting.
    /// The first token is the executable, the rest are arguments.
    /// </summary>
    internal static IReadOnlyList<string> SplitCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Array.Empty<string>();

        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inSingle = false;
        var inDouble = false;

        foreach (var ch in command)
        {
            if (inSingle)
            {
                if (ch == '\'')
                    inSingle = false;
                else
                    current.Append(ch);
            }
            else if (inDouble)
            {
                if (ch == '"')
                    inDouble = false;
                else
                    current.Append(ch);
            }
            else if (ch == '\'')
            {
                inSingle = true;
            }
            else if (ch == '"')
            {
                inDouble = true;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts;
    }
}
