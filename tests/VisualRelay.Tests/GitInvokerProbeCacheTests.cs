using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Guards that <see cref="GitInvoker"/> caches its git-binary resolution
/// process-wide so the first instance pays the probe and every later
/// instance reuses the resolved path.
/// </summary>
public sealed class GitInvokerProbeCacheTests
{
    [Fact]
    public async Task ProbeCache_WarmsOnceAcrossInstances()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"git-probe-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            // Record the probe count before constructing any invokers.
            // Other tests may have already warmed the cache, so we only
            // assert the count does NOT increase between our two calls.
            var countBefore = GitInvoker.ProbeCount;

            var invoker1 = new GitInvoker();
            await invoker1.RunAsync(tmpDir, ["--version"], CancellationToken.None);
            var countAfter1 = GitInvoker.ProbeCount;

            var invoker2 = new GitInvoker();
            await invoker2.RunAsync(tmpDir, ["--version"], CancellationToken.None);
            var countAfter2 = GitInvoker.ProbeCount;

            // If the cache works, the second construction reuses the resolved
            // binary without re-probing; if it doesn't, countAfter2 > countAfter1.
            Assert.True(countAfter2 == countAfter1,
                $"ProbeCount increased from {countAfter1} to {countAfter2} across two " +
                $"GitInvoker constructions (baseline was {countBefore}). " +
                "Expected the probe to run at most once.");
        }
        finally
        {
            TestFileSystem.DeleteDirectoryResilient(tmpDir);
        }
    }
}
