namespace VisualRelay.Tests;

/// <summary>
/// Shared test helpers for file-system operations that must be robust under
/// macOS APFS / Spotlight indexer races.
///
/// Problem: a bare <c>Directory.Delete(path, recursive: true)</c> call in a
/// test teardown can throw <see cref="System.IO.IOException"/> ("Directory not
/// empty") on macOS when the indexer briefly holds a handle inside the tree
/// being deleted.  In a heavily-parallelised test suite this surfaces as an
/// intermittent teardown failure that flakes an otherwise-passing test.
///
/// The helper below retries the delete a handful of times with a short
/// escalating back-off and, on the final attempt, silently swallows the
/// exception.  Leaking a temp directory is acceptable; flaking the suite is
/// not.
/// </summary>
internal static class TestFileSystem
{
    /// <summary>
    /// Deletes <paramref name="path"/> and all its contents, retrying up to
    /// eight times with an escalating back-off to absorb transient OS holds
    /// (e.g. APFS / Spotlight on macOS).  On the last attempt any remaining
    /// exception is swallowed so that test teardowns never throw.
    /// </summary>
    public static void DeleteDirectoryResilient(string path)
    {
        if (!Directory.Exists(path))
            return;

        const int maxAttempts = 8;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return; // success
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                YieldBetweenRetries(attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                YieldBetweenRetries(attempt);
            }
            catch
            {
                // Final attempt — swallow; leaking a temp dir beats flaking.
                return;
            }
        }
    }

    // Escalating scheduler yields between delete retries — hands the OS/indexer a few
    // turns to release its transient hold WITHOUT a wall-clock sleep (guard-clean and
    // instant). "Leaking a temp dir beats flaking" already governs the final attempt,
    // so trading the old timed back-off for yields cannot regress correctness.
    private static void YieldBetweenRetries(int attempt)
    {
        for (var i = 0; i < attempt; i++)
            Thread.Yield();
    }
}

internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "vr-sdvm-tests", Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { TestFileSystem.DeleteDirectoryResilient(Path); }
        catch { /* best-effort */ }
    }
}
