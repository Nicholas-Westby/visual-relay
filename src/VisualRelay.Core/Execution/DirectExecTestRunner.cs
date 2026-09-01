using System.ComponentModel;
using System.Diagnostics;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Runs a test command via direct exec (no /bin/sh -lc wrapper) so exit code 127
/// (command-not-found) is surfaced reliably. Time-boxed by a configurable timeout.
/// </summary>
public sealed class DirectExecTestRunner(TimeSpan? timeout = null) : ITestRunner
{
    private static readonly char[] PathSeparators = ['/', '\\'];

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(5);

    public async Task<TestRunResult> RunAsync(
        string rootPath,
        string command,
        CancellationToken cancellationToken = default)
    {
        // Split on whitespace: first token is the executable, remainder are args.
        var parts = SplitCommand(command);
        if (parts.Count == 0)
        {
            return new TestRunResult(127, string.Empty);
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
                ResolveProgram(parts[0], rootPath),
                parts.Skip(1),
                rootPath,
                _timeout,
                cancellationToken);

            return new TestRunResult(exitCode, output, timedOut, sw.Elapsed);
        }
        catch (Win32Exception)
        {
            // ENOENT — file not found. Map to exit 127 (shell convention for
            // command-not-found) with no output.
            return new TestRunResult(127, string.Empty);
        }
    }

    /// <summary>
    /// Anchors a repo-relative program (<c>./gradlew</c>, <c>./scripts/test.sh</c>)
    /// to <paramref name="rootPath"/>. .NET resolves a relative
    /// <see cref="ProcessStartInfo.FileName"/> against the CALLING process's
    /// current directory, not against <see cref="ProcessStartInfo.WorkingDirectory"/>,
    /// so an unanchored <c>./x</c> raises ENOENT (surfaced here as exit 127) even
    /// though the file sits in the repo root. The pipeline runs the same command
    /// through <c>/bin/sh -lc</c> with the repo as cwd, where <c>./x</c> resolves —
    /// so without this the smoke-validation rejects commands that run perfectly
    /// well later. A bare name (<c>pytest</c>) is left alone for PATH lookup, and
    /// an already-rooted path is left alone too.
    /// </summary>
    private static string ResolveProgram(string program, string rootPath) =>
        !Path.IsPathRooted(program) && program.IndexOfAny(PathSeparators) >= 0
            ? Path.GetFullPath(Path.Combine(rootPath, program))
            : program;

    /// <summary>
    /// Splits a command string on whitespace, respecting simple quoting.
    /// The first token is the executable, the rest are arguments.
    /// </summary>
    private static IReadOnlyList<string> SplitCommand(string command)
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

    /// <summary>
    /// Resolves a command string into (FileName, Arguments) suitable for
    /// direct exec.  Exposed for <see cref="SandboxedTestRunner"/>.
    /// </summary>
    internal static (string FileName, IReadOnlyList<string> Arguments) ResolveLaunch(string command)
    {
        var parts = SplitCommand(command);
        if (parts.Count == 0)
            return (string.Empty, Array.Empty<string>());
        return (parts[0], parts.Skip(1).ToList());
    }
}
