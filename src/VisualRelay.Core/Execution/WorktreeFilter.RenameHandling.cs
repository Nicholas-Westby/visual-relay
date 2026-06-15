namespace VisualRelay.Core.Execution;

internal static partial class WorktreeFilter
{
    /// <summary>
    /// Compute the set of staged-rename/copy endpoints to EXCLUDE from
    /// reversion when one side of the pair is a declared testFile.  The rule
    /// is asymmetric:
    /// <list type="bullet">
    /// <item>The DESTINATION is always excluded (when either side is a
    ///   testFile) — it may hold the only surviving copy of the test content,
    ///   so it must never be clobbered.</item>
    /// <item>The SOURCE is excluded ONLY when the source is itself a testFile.
    ///   For a prod→test rename the source is NOT excluded, so the caller
    ///   reverts it (restoring the production file and undoing its staged
    ///   deletion) — leak 4.  Restoring a distinct source path does not touch
    ///   the test destination, so there is no test-content loss.</item>
    /// </list>
    /// <para>
    /// <paramref name="renamePairs"/> is a flat list of <c>(old, new)</c> raw
    /// git names — NOT a dictionary.  A dictionary keyed by the host-gated
    /// comparer collapses a case-only rename (<c>Foo.cs</c> ↔ <c>foo.cs</c>)
    /// into one self-referential entry under OrdinalIgnoreCase, after which a
    /// <c>CompareOrdinal(key,value) &gt;= 0</c> dedup gate skips it and the
    /// exclusion never fires.  Using a list preserves both distinct raw names
    /// so a case-only rename whose endpoint is a testFile is still handled
    /// (Defect A residual / Hole 1).
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
            var oldIsTest = testSet.Contains(NormalizeRepoRelativePath(oldName));
            var newIsTest = testSet.Contains(NormalizeRepoRelativePath(newName));
            if (!oldIsTest && !newIsTest)
                continue;   // neither side a testFile → caller reverts in full.

            // The DESTINATION (new name) is always excluded when either side is
            // a testFile: it may hold the only surviving copy of the test
            // content, so it must never be clobbered.  Iterating the flat list
            // (not a comparer-keyed dictionary) means a case-only rename does
            // not collapse into one self-referential entry, and there is no
            // CompareOrdinal dedup gate to skip it (Hole 1).
            exclude.Add(newName);

            // The SOURCE (old name) is excluded ONLY when it is itself a
            // testFile.  Leak 4: for a prod→test rename (source is production,
            // destination is a testFile) excluding the source too would leave
            // its staged DELETION surviving into stage 6 — a production change
            // leaking past the filter.  By NOT excluding the source here it
            // flows into nonTestTracked and is reverted via `git checkout
            // HEAD -- <source>`, which restores the production file (reverting
            // the staged deletion) WITHOUT touching the test destination (a
            // distinct path).  When the source IS a testFile (test→test or
            // test→prod) we still exclude it, so the rename of a test file is
            // left intact and the source is not restored.
            if (oldIsTest)
                exclude.Add(oldName);
        }

        return exclude;
    }
}
