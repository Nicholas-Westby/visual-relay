using VisualRelay.Core.Execution;

namespace VisualRelay.Guards;

/// <summary>
/// CLI runner for the enforcing shell-script size guard: lists git-tracked files,
/// reads them, and reports shell scripts whose logic-line count exceeds the limit
/// (default 20, <c>--max N</c> or <c>VISUAL_RELAY_SHELL_LINE_LIMIT</c>). Exits
/// non-zero (1) when any script is over the limit. Git is routed through
/// <see cref="GitInvoker"/>.
/// </summary>
public static class ShellSizeGuardRunner
{
    public static async Task<int> RunAsync(string repoRoot, string[] args)
    {
        var limit = ResolveLimit(args);

        var git = new GitInvoker();
        var (exitCode, output, timedOut) = await git.RunAsync(repoRoot, ["ls-files"], CancellationToken.None);
        if (exitCode != 0 || timedOut)
        {
            Console.Error.WriteLine("shell-size: git ls-files failed (exit " + exitCode + ")");
            return 1; // enforcing — a failed enumeration is a failure, not a pass
        }

        var trackedFiles = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.TrimEnd('\r'))
            .ToArray();

        var fileData = new List<(string Path, string[] Lines)>();
        foreach (var file in trackedFiles)
        {
            var fullPath = Path.Combine(repoRoot, file);
            if (!File.Exists(fullPath))
                continue;
            try
            {
                fileData.Add((file, File.ReadAllLines(fullPath)));
            }
            catch
            {
                // skip unreadable files (e.g. permissions)
            }
        }

        var violations = ShellSizeGuard.FindViolations(fileData, limit);
        foreach (var v in violations)
        {
            Console.WriteLine($"{v.Path}: {v.Count} logic lines (limit {v.Limit})");
            Console.WriteLine("  → move the logic into a C# tool and leave a thin wrapper; there is no allowlist.");
        }

        Console.WriteLine($"shell-size: {violations.Count} script(s) over the limit.");
        return violations.Count > 0 ? 1 : 0;
    }

    private static int ResolveLimit(string[] args)
    {
        // The shared default + env var (so the gate and the report never diverge),
        // with an additional ad-hoc --max override for this runner.
        var limit = ShellSizeGuard.ResolveLimit();
        var maxArgIndex = Array.IndexOf(args, "--max");
        if (maxArgIndex >= 0 && maxArgIndex + 1 < args.Length
            && int.TryParse(args[maxArgIndex + 1], out var parsed))
        {
            limit = parsed;
        }

        return limit;
    }
}
