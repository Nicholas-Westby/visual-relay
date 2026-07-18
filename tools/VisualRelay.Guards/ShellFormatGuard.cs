using VisualRelay.Core.Execution;

namespace VisualRelay.Guards;

/// <summary>
/// Enforces shfmt formatting on shell scripts. Runs <c>shfmt --diff</c> (bare,
/// no style flags — <c>.editorconfig</c> is the canonical style source; today no
/// shell section matches, so output is shfmt defaults: tabs). Exit 0 → clean;
/// exit 1 → diff text; missing binary → failure.
/// </summary>
public static class ShellFormatGuard
{
    /// <summary>
    /// Result of a format check. <see cref="Clean"/> is true when every file
    /// is already shfmt-formatted (exit 0); <see cref="Output"/> carries the
    /// diff when formatting drift is detected; <see cref="Error"/> carries a
    /// diagnostic when shfmt is missing or the check fails.
    /// </summary>
    public sealed record ShellFormatResult(bool Clean, string? Output, string? Error);

    /// <summary>
    /// Runs <c>shfmt --diff</c> over <paramref name="filePaths"/> and returns
    /// the result. A missing shfmt binary is a failure (enforcing gate).
    /// </summary>
    public static async Task<ShellFormatResult> CheckAsync(
        string repoRoot,
        IReadOnlyList<string> filePaths,
        CancellationToken ct)
    {
        if (filePaths.Count == 0)
            return new ShellFormatResult(true, null, null);

        if (!PathExecutables.OnPath("shfmt"))
        {
            return new ShellFormatResult(
                false, null,
                "shfmt not found; run through ./visual-relay so the nix devshell provides it");
        }

        var args = new List<string> { "--diff", "--" };
        args.AddRange(filePaths);

        try
        {
            var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
                "shfmt", args, repoRoot, TimeSpan.FromSeconds(30), ct);

            if (timedOut)
            {
                return new ShellFormatResult(false, null, "shfmt --diff timed out");
            }

            return exitCode switch
            {
                0 => new ShellFormatResult(true, null, null),
                1 => new ShellFormatResult(false, output.TrimEnd(), null),
                _ => new ShellFormatResult(false, null,
                    $"shfmt --diff failed with exit code {exitCode}")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ShellFormatResult(false, null,
                $"shfmt --diff failed: {ex.Message}");
        }
    }
}
