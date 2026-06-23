using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Ensures the <c>VisualRelay.CommandGuard</c> middleware binary is published
/// to <c>&lt;repoRoot&gt;/command-guard/</c> before a run starts, so the
/// swival <c>--command-middleware</c> wrapper can exec it from inside the
/// nono sandbox.
///
/// <para>Publishes as a <b>self-contained</b> binary so no .NET runtime
/// discovery is needed in the sandbox.  Mirrors the pattern of
/// <see cref="NonoProfileEnsurer"/>: called once at run start in
/// <see cref="RelayDriver"/>.  Fail-open: a missing dotnet or publish
/// failure logs a warning and returns — the middleware path is only wired
/// when the wrapper exists, so a missing/broken publish degrades to the
/// squash backstop.</para>
/// </summary>
public static class CommandGuardEnsurer
{
    private const string PublishedDirName = "command-guard";
    private const string ProjectName = "VisualRelay.CommandGuard";

    /// <summary>
    /// Publishes <c>tools/VisualRelay.CommandGuard</c> to
    /// <c>&lt;repoRoot&gt;/command-guard/</c> as a self-contained binary.
    /// Skips the publish when the binary is already up to date (project
    /// sources older than output).
    /// Returns the path to the published binary, or null when publishing
    /// could not be performed (fail-open: the run continues).
    /// </summary>
    public static async Task<string?> EnsureAsync(
        string repoRoot, CancellationToken cancellationToken = default)
    {
        var publishDir = Path.GetFullPath(Path.Combine(repoRoot, PublishedDirName));
        var binaryPath = ResolveBinaryPath(publishDir);

        // Guard: only publish when the project source exists (repo checkout).
        var projectDir = Path.GetFullPath(Path.Combine(repoRoot, "tools", ProjectName));
        if (!Directory.Exists(projectDir))
        {
            // No source — can't publish. This is normal for a
            // self-contained / installed deployment. Fail-open.
            return File.Exists(binaryPath) ? binaryPath : null;
        }

        // Incremental: skip publish when binary is up to date.
        if (File.Exists(binaryPath) && IsUpToDate(projectDir, binaryPath))
            return binaryPath;

        // Try publishing via dotnet.  Publish self-contained so the
        // apphost carries its own runtime and needs no DOTNET_ROOT inside
        // the nono sandbox.
        try
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList =
                {
                    "publish", projectDir,
                    "--self-contained", "-r", rid,
                    "-o", publishDir,
                    "--nologo", "-v", "q"
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return Fallback(binaryPath);

            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
                return binaryPath;

            // Publish failed — log but don't throw (fail-open).
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await Console.Error.WriteLineAsync(
                $"CommandGuardEnsurer: dotnet publish failed (exit {process.ExitCode}): {stderr.Trim()}");
            return Fallback(binaryPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync(
                $"CommandGuardEnsurer: dotnet publish error: {ex.Message}");
            return Fallback(binaryPath);
        }
    }

    /// <summary>
    /// Resolves the expected binary path for the current platform.
    /// </summary>
    private static string ResolveBinaryPath(string publishDir) =>
        Path.Combine(publishDir, ProjectName);

    private static string? Fallback(string binaryPath) =>
        File.Exists(binaryPath) ? binaryPath : null;

    /// <summary>
    /// Returns true when the binary is newer than every source file in the
    /// project directory (recursive). This is a fast, best-effort check
    /// — a full build graph would be overkill.
    /// </summary>
    private static bool IsUpToDate(string projectDir, string binaryPath)
    {
        try
        {
            var binaryTime = File.GetLastWriteTimeUtc(binaryPath);
            var files = Directory.GetFiles(projectDir, "*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                if (File.GetLastWriteTimeUtc(file) > binaryTime)
                    return false;
            }

            return true;
        }
        catch
        {
            return false; // if we can't check, publish to be safe
        }
    }
}
