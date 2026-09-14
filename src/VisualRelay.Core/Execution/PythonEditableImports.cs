namespace VisualRelay.Core.Execution;

/// <summary>
/// What a snapshot of a checkout puts on the Python import path so the project under test is
/// imported from the snapshot. uv, pip, hatch and poetry install a project editable as a
/// <c>.pth</c> line holding the checkout's absolute source directory, and a virtualenv overlaid
/// into a snapshot (cloned, copied or linked) keeps that line, so a run there imported the
/// checkout's code. Measured on openai-agents-python: 35 tests that check traceback frames lie
/// under their own src/agents failed in the verify snapshot and passed in the checkout.
/// </summary>
internal static class PythonEditableImports
{
    /// <summary>
    /// Each absolute <c>.pth</c> line of a virtualenv among <paramref name="ignoredDirectories"/> that
    /// names <paramref name="checkoutSeen"/> or a directory under it, re-rooted at
    /// <paramref name="snapshotSeen"/>, in the order Python adds them, each once. The files are read
    /// under <paramref name="checkoutPath"/>; the two seen paths are the checkout and the snapshot as
    /// the sandbox names them, which is the form the lines hold. Unreadable virtualenvs are skipped.
    /// </summary>
    internal static IReadOnlyList<string> SnapshotRoots(
        string checkoutPath, IEnumerable<string> ignoredDirectories, string checkoutSeen, string snapshotSeen)
    {
        var checkout = checkoutSeen.TrimEnd('/', '\\');
        var snapshot = snapshotSeen.TrimEnd('/', '\\');
        var roots = new List<string>();
        if (checkout.Length == 0)
            return roots;
        foreach (var name in ignoredDirectories)
        {
            var venv = Path.Combine(checkoutPath, name);
            try
            {
                if (!File.Exists(Path.Combine(venv, "pyvenv.cfg")))
                    continue;
                foreach (var line in PthFiles(venv).SelectMany(File.ReadLines))
                {
                    var entry = line.Trim();
                    if (!IsAtOrUnder(entry, checkout))
                        continue;
                    var root = snapshot + entry[checkout.Length..];
                    if (!roots.Contains(root))
                        roots.Add(root);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A virtualenv that cannot be read leaves its imports as they were.
            }
        }

        return roots;
    }

    /// <summary>The search-path variables that put <paramref name="roots"/> first; empty for none.</summary>
    internal static IReadOnlyDictionary<string, string> SearchPaths(IReadOnlyList<string> roots) =>
        roots.Count == 0
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(StringComparer.Ordinal) { ["PYTHONPATH"] = string.Join(':', roots) };

    // Python reads a site directory's .pth files in name order.
    private static IEnumerable<string> PthFiles(string venv)
    {
        var lib = Path.Combine(venv, "lib");
        return Directory.Exists(lib)
            ? Directory.EnumerateDirectories(lib, "python*")
                .Select(version => Path.Combine(version, "site-packages"))
                .Where(Directory.Exists)
                .SelectMany(sitePackages => Directory.EnumerateFiles(sitePackages, "*.pth").Order(StringComparer.Ordinal))
            : [];
    }

    private static bool IsAtOrUnder(string entry, string directory) =>
        entry.StartsWith(directory, StringComparison.Ordinal)
        && (entry.Length == directory.Length || entry[directory.Length] is '/' or '\\');
}
