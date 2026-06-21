using VisualRelay.Core.Execution;

namespace VisualRelay.Guards;

/// <summary>
/// CLI runner for the advisory shell-script size guard: lists git-tracked files,
/// reads them, and reports shell scripts whose logic-line count exceeds the limit
/// (default 20, <c>--max N</c> or <c>VISUAL_RELAY_SHELL_LINE_LIMIT</c>). Advisory —
/// always exits 0. Git is routed through <see cref="GitInvoker"/>.
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
            return 0; // advisory — exit 0
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
        return 0;
    }

    private static int ResolveLimit(string[] args)
    {
        var limit = 20;
        var maxArgIndex = Array.IndexOf(args, "--max");
        if (maxArgIndex >= 0 && maxArgIndex + 1 < args.Length
            && int.TryParse(args[maxArgIndex + 1], out var parsed))
        {
            limit = parsed;
        }

        var envLimit = Environment.GetEnvironmentVariable("VISUAL_RELAY_SHELL_LINE_LIMIT");
        if (envLimit is not null && int.TryParse(envLimit, out var envParsed))
        {
            limit = envParsed;
        }

        return limit;
    }
}
