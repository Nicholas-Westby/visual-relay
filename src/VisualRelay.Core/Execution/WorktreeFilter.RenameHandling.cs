namespace VisualRelay.Core.Execution;

internal static partial class WorktreeFilter
{
    /// <summary>
    /// Compute the set of staged-rename endpoints to EXCLUDE from reversion
    /// because one side of the rename is a declared testFile.  Excluding both
    /// endpoints leaves the rename intact: the destination (which may hold the
    /// only surviving copy of the test content) is not clobbered, and the
    /// source is not restored alongside it (no index pollution).
    /// <para>
    /// <paramref name="renamePairs"/> is a flat list of <c>(old, new)</c> raw
    /// git names — NOT a dictionary.  A dictionary keyed by the host-gated
    /// comparer collapses a case-only rename (<c>Foo.cs</c> ↔ <c>foo.cs</c>)
    /// into one self-referential entry under OrdinalIgnoreCase, after which a
    /// <c>CompareOrdinal(key,value) &gt;= 0</c> dedup gate skips it and the
    /// exclusion never fires.  Using a list preserves both distinct raw names
    /// so a case-only rename whose endpoint is a testFile still excludes BOTH
    /// endpoints (Defect A residual / Hole 1).
    /// </para>
    /// When NEITHER endpoint is a testFile the pair is omitted from the
    /// exclusion set, so the rename is still fully reverted by the caller.
    /// </summary>
    internal static HashSet<string> ComputeRenameExclusions(
        IReadOnlyList<(string Old, string New)> renamePairs,
        IReadOnlySet<string> testSet,
        StringComparer pathComparer)
    {
        var exclude = new HashSet<string>(pathComparer);
        foreach (var (oldName, newName) in renamePairs)
        {
            // Exclude BOTH raw endpoints when EITHER side is a declared testFile.
            // Iterating the flat list (rather than a comparer-keyed dictionary)
            // means a case-only rename does not collapse into a single
            // self-referential entry, and there is no CompareOrdinal dedup gate
            // to skip it — so both endpoints are still excluded (Hole 1).  When
            // neither side is a testFile the pair is omitted, so the caller still
            // reverts the rename in full (distinct-name regression preserved).
            if (testSet.Contains(NormalizeRepoRelativePath(oldName))
                || testSet.Contains(NormalizeRepoRelativePath(newName)))
            {
                exclude.Add(oldName);
                exclude.Add(newName);
            }
        }

        return exclude;
    }
}
