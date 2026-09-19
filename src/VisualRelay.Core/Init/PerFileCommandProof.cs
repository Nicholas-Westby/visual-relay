using VisualRelay.Core.Execution;

namespace VisualRelay.Core.Init;

/// <summary>Whether a per-file command was proven, and why not when it was not.</summary>
/// <param name="Proven">True when the command ran the chosen files and passed.</param>
/// <param name="Reason">Why it was rejected, or null when it was proven.</param>
/// <param name="OutputHead">The first lines of what it printed, for the setup-check log.</param>
public sealed record PerFileProofResult(bool Proven, string? Reason, string OutputHead)
{
    internal static PerFileProofResult Ok() => new(true, null, string.Empty);

    internal static PerFileProofResult Rejected(string reason, string output = "") =>
        new(false, reason, Head(output));

    private static string Head(string output) =>
        string.Join('\n', output.Split('\n').Take(20)).TrimEnd();
}

/// <summary>
/// Proves a per-file test command before bootstrap writes it. The author-tests gate
/// runs only the test files a task just wrote, through <c>testFileCmd</c>, and nothing
/// ever ran that command before the first task depended on it. Where the table's form
/// is wrong the gate then fails for the wrong reason — a red that comes from a broken
/// command is not proof that the new tests fail — and where the table has no entry the
/// gate runs the whole suite on every task.
/// <para>
/// Visual Relay is meant for repositories whose suite is green, so an existing test
/// file, run alone, passes. That is the signal: substitute real test files and require
/// a clean run.
/// </para>
/// </summary>
internal static class PerFileCommandProof
{
    /// <summary>Two, so a form that only works for one file is caught; one when the repository has one.</summary>
    private const int FilesToSubstitute = 2;

    internal static async Task<PerFileProofResult> ProveAsync(
        string rootPath,
        string perFileCommand,
        IReadOnlyList<string> testFiles,
        ITestRunner runner,
        CancellationToken cancellationToken)
    {
        if (!perFileCommand.Contains("{files}", StringComparison.Ordinal))
            return PerFileProofResult.Rejected("the command has no {files} token");
        if (testFiles.Count == 0)
            return PerFileProofResult.Rejected("the repository has no test file to prove it with");

        var chosen = testFiles.Take(FilesToSubstitute).ToList();
        var command = perFileCommand.Replace("{files}", string.Join(' ', chosen), StringComparison.Ordinal);
        var result = await runner.RunAsync(rootPath, command, cancellationToken);

        if (result.TimedOut)
            return PerFileProofResult.Rejected("the command timed out", result.Output);
        if (GateUsability.IsUnusable(result))
            return PerFileProofResult.Rejected("the command ran no tests", result.Output);
        return result.ExitCode == 0
            ? PerFileProofResult.Ok()
            : PerFileProofResult.Rejected($"the command exited {result.ExitCode}", result.Output);
    }

    /// <summary>
    /// The repository's own test files, smallest first, so the proof is quick. Only
    /// paths the gate itself would accept as test files count.
    /// </summary>
    /// <param name="rootPath">The repository root.</param>
    /// <param name="trackedPaths">The tracked file list.</param>
    /// <param name="testPaths">The configured test paths, if any.</param>
    /// <returns>At most <see cref="FilesToSubstitute"/> paths.</returns>
    internal static IReadOnlyList<string> ChooseTestFiles(
        string rootPath, IReadOnlyList<string> trackedPaths, IReadOnlyList<string>? testPaths) =>
    [
        .. trackedPaths
            .Where(path => TestPathClassifier.IsRunnableTestFile(path, testPaths))
            .Select(path => (Path: path, Size: SizeOf(Path.Combine(rootPath, path))))
            .Where(entry => entry.Size >= 0)
            .OrderBy(entry => entry.Size)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .Take(FilesToSubstitute)
            .Select(entry => entry.Path),
    ];

    private static long SizeOf(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}
