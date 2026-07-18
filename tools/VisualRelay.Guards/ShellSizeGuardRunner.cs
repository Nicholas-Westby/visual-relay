using VisualRelay.Core.Execution;

namespace VisualRelay.Guards;

/// <summary>
/// CLI runner for the enforcing shell-script size guard: lists git-tracked files,
/// reads them, and reports shell scripts whose logic-line count exceeds the limit
/// (default 24, <c>--max N</c> or <c>VISUAL_RELAY_SHELL_LINE_LIMIT</c> overrides
/// only the general limit; the <c>visual-relay</c> bootstrap has a fixed 100-line
/// carve-out). Also runs <c>shfmt --diff</c> via <see cref="ShellFormatGuard"/> to
/// flag formatting drift. Exits non-zero (1) when any script is over its limit or
/// formatting has drifted.
/// </summary>
public static class ShellSizeGuardRunner
{
    public static async Task<int> RunAsync(string repoRoot, string[] args)
    {
        var limit = ResolveLimit(args);

        List<(string Path, string[] Lines)> tracked;
        try
        {
            tracked = await TrackedShellScripts.EnumerateAsync(repoRoot, new GitInvoker());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"shell-size: {ex.Message}");
            return 1;
        }

        var violations = ShellSizeGuard.FindViolations(tracked, limit);
        foreach (var v in violations)
        {
            Console.WriteLine($"{v.Path}: {v.Count} logic lines (limit {v.Limit})");
            Console.WriteLine("  → move the logic into a C# tool and leave a thin wrapper; the bootstrap (visual-relay) is the single structural carve-out.");
        }

        var formatPaths = tracked.Select(t => t.Path).ToList();
        var formatResult = await ShellFormatGuard.CheckAsync(repoRoot, formatPaths, CancellationToken.None);

        if (!formatResult.Clean)
        {
            if (formatResult.Error is not null)
            {
                Console.Error.WriteLine($"shell-format: {formatResult.Error}");
            }
            else if (formatResult.Output is not null)
            {
                Console.WriteLine(formatResult.Output);
                Console.WriteLine("  → run ./visual-relay format");
            }
        }

        var hasViolations = violations.Count > 0 || !formatResult.Clean;
        Console.WriteLine($"shell-size: {violations.Count} script(s) over the limit; format {(formatResult.Clean ? "clean" : "dirty")}.");
        return hasViolations ? 1 : 0;
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
