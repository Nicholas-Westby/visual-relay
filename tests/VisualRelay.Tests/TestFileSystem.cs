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
                Thread.Sleep(attempt * 25); // 25 ms, 50 ms, 75 ms …
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts)
            {
                Thread.Sleep(attempt * 25);
            }
            catch
            {
                // Final attempt — swallow; leaking a temp dir beats flaking.
                return;
            }
        }
    }
}
