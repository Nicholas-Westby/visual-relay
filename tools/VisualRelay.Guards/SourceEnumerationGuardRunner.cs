using VisualRelay.Core.Execution;

namespace VisualRelay.Guards;

/// <summary>
/// CLI runner for <see cref="SourceEnumerationGuard"/> (ports
/// <c>guard-source-enumeration.sh</c>): exits 2 (printing the cause+remedy to
/// stderr) on a stale virtio-fs/readdir cache, else 0. Git via <see cref="GitInvoker"/>.
/// </summary>
public static class SourceEnumerationGuardRunner
{
    public static async Task<int> RunAsync(string repoRoot)
    {
        var (exitCode, message) = await SourceEnumerationGuard.RunAsync(repoRoot, new GitInvoker());
        if (exitCode != 0)
            Console.Error.WriteLine(message);
        return exitCode;
    }
}
