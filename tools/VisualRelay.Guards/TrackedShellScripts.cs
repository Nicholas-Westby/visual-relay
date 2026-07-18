using VisualRelay.Core.Execution;

namespace VisualRelay.Guards;

/// <summary>
/// Shared enumeration helper: lists git-tracked files via <see cref="IGitInvoker"/>,
/// filters them through <see cref="ShellScriptClassifier"/>, and returns only the
/// (relativePath, lines) pairs that are shell scripts. Missing/unreadable files are
/// silently skipped.
/// </summary>
public static class TrackedShellScripts
{
    /// <summary>
    /// Enumerates every git-tracked shell script under <paramref name="repoRoot"/>.
    /// Throws when <c>git ls-files</c> fails — callers catch and surface.
    /// </summary>
    public static async Task<List<(string Path, string[] Lines)>> EnumerateAsync(
        string repoRoot, IGitInvoker git)
    {
        var (exitCode, output, timedOut) =
            await git.RunAsync(repoRoot, ["ls-files"], CancellationToken.None);
        if (exitCode != 0 || timedOut)
        {
            throw new InvalidOperationException(
                $"git ls-files failed (exit {exitCode}" + (timedOut ? ", timed out" : "") + ")");
        }

        var files = new List<(string Path, string[] Lines)>();
        foreach (var rel in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                     .Select(f => f.TrimEnd('\r')))
        {
            var fullPath = Path.Combine(repoRoot, rel);
            if (!File.Exists(fullPath))
                continue;
            try
            {
                var lines = File.ReadAllLines(fullPath);
                var firstLine = lines.Length > 0 ? lines[0] : null;
                if (ShellScriptClassifier.IsShellScript(rel, firstLine))
                    files.Add((rel, lines));
            }
            catch
            {
                // skip unreadable files
            }
        }

        return files;
    }
}
