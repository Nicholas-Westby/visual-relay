namespace VisualRelay.Core.Execution;

internal static partial class WorktreeFilter
{
    /// <summary>
    /// Strip the single trailing line-feed that the line-oriented
    /// <see cref="ProcessCapture"/> reader appends to the whole captured blob
    /// (its <c>AppendLine</c> re-terminates the final read).  Git's <c>-z</c>
    /// output ends every path with a NUL and emits NO trailing newline of its
    /// own, so the only <c>\n</c> after the final NUL is that capture artifact.
    /// <para>
    /// Embedded newlines INSIDE a path are preserved: the reader splits stdout
    /// on <c>\n</c> but <c>AppendLine</c> re-inserts each one, so a path that
    /// itself contains (or even ends with) a newline round-trips intact before
    /// its NUL terminator — only the one trailing artifact is removed.  Tokens
    /// are then taken as the NUL-separated content; the empty segment after the
    /// final NUL is dropped by the callers' length checks.
    /// </para>
    /// <para>
    /// KNOWN LIMITATION (document only — not fixed): because the blob is read
    /// line-buffered by <see cref="ProcessCapture"/>, which FOLDS a carriage
    /// return (<c>\r</c>) into <c>\n</c>, a path that contains a literal
    /// <c>\r</c>, and any host that emits Windows <c>\r\n</c> line endings, are
    /// NOT handled here — the <c>\r</c> bytes are already lost before this
    /// splitter runs.  Correct handling would need a byte-level reader; that
    /// change is deliberately avoided (high blast radius, exotic input, and the
    /// harness runs on macOS/Linux where git emits bare <c>\n</c>).  Embedded
    /// TAB / leading-trailing space / embedded-LF ARE handled; embedded-CR and
    /// CRLF hosts are the explicit exception.
    /// </para>
    /// </summary>
    private static string[] SplitNulRecords(string output)
    {
        // Remove exactly one trailing capture-appended '\n' (never more — a
        // path may legitimately end in a newline before its NUL).
        if (output.EndsWith('\n'))
            output = output[..^1];
        return output.Split('\0');
    }

    /// <summary>
    /// Append the paths from a NUL-delimited (<c>-z</c>) plain path list —
    /// the output of <c>git diff --name-only -z</c>, <c>git ls-files
    /// --deleted -z</c>, and <c>git ls-files --others -z</c> — to
    /// <paramref name="target"/>.
    /// <para>
    /// <c>-z</c> emits each path verbatim, terminated by a NUL, and NEVER
    /// applies C-style quoting (so a TAB or newline in a path stays literal)
    /// nor any whitespace stripping (so a leading/trailing space survives).
    /// Splitting on <c>\0</c> — and crucially NOT calling <c>Trim()</c> on the
    /// result — is what fixes the quoted-path (leak 1) and whitespace-path
    /// (leak 2) misses.  Records are separated, not terminated only at the
    /// end, so a trailing empty segment after the final NUL is discarded.
    /// </para>
    /// </summary>
    private static void AddNulPaths(string output, List<string> target)
    {
        foreach (var path in SplitNulRecords(output))
        {
            // Do NOT Trim — a path may legitimately start or end with a space
            // (leak 2).  Only the empty trailing segment after the last NUL
            // (and any accidental blank) is skipped.
            if (path.Length > 0) target.Add(path);
        }
    }

    /// <summary>
    /// Parse the NUL-delimited (<c>-z</c>) output of
    /// <c>git diff --cached --name-status -M -C -z</c>.
    /// <para>
    /// The <c>-z</c> name-status stream is a flat sequence of NUL-separated
    /// tokens, NOT newline records: a plain change is <c>status\0path\0</c>;
    /// a rename or copy is <c>Xnn\0old\0new\0</c> — the status, then TWO
    /// NUL-separated paths and NO embedded TAB (unlike the non-<c>-z</c>
    /// form).  Renames carry status <c>R</c> and copies status <c>C</c>;
    /// both have the same three-token shape, so both are parsed as a
    /// three-token record (leak 3 — a <c>C</c> record's destination was
    /// previously dropped into a 2-part mis-parse).  <c>-C</c> is kept on the
    /// command precisely so a copy SURFACES as a <c>C</c> record (<c>-M</c>
    /// alone suppresses copy detection); the difference is in how each is
    /// treated downstream:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Rename (<c>R</c>)</b> — git DELETES the source, so the
    ///   destination may hold the only surviving copy of the source's
    ///   content.  Both endpoints are added to <paramref name="target"/> AND
    ///   recorded as a pair in <paramref name="renamePairs"/> so
    ///   <see cref="WorktreeFilter.ComputeRenameExclusions"/> can protect the
    ///   destination (and the leak-4 / case-only-rename logic can act).</item>
    /// <item><b>Copy (<c>C</c>)</b> — git does NOT delete the source; the
    ///   source still exists on disk and in the index.  The copy's
    ///   DESTINATION is therefore just an ordinary staged addition, so it is
    ///   added to <paramref name="target"/> (to be reverted/deleted as a
    ///   normal entry) but is NEVER recorded in <paramref name="renamePairs"/>
    ///   — it must not be rename-protected, or a copy from a testFile to a
    ///   production path would leak that production destination into stage 6
    ///   (review follow-up B-1).</item>
    /// </list>
    /// <para>
    /// KNOWN LIMITATION (parsing, not fixed — document only): the <c>-z</c>
    /// stream is read through <see cref="ProcessCapture"/>, which is
    /// line-buffered and folds a carriage-return (<c>\r</c>) into <c>\n</c>.
    /// A path that itself contains a <c>\r</c>, and any host that emits
    /// Windows <c>\r\n</c> line endings, are therefore NOT handled correctly
    /// here (nor by <see cref="SplitNulRecords"/>); a byte-level reader would
    /// be required.  This is left as-is on purpose — the harness runs on
    /// macOS/Linux where git's <c>-z</c> output uses bare <c>\n</c>, and
    /// changing <see cref="ProcessCapture"/> has a high blast radius for an
    /// exotic input.  So the surrounding "all special chars" claims cover
    /// TAB / leading-trailing-space / embedded-LF, but explicitly NOT
    /// embedded-CR or CRLF hosts.
    /// </para>
    /// </summary>
    private static void AddNameStatusNul(
        string output,
        List<string> target,
        List<(string Old, string New)> renamePairs)
    {
        var tokens = SplitNulRecords(output);
        var i = 0;
        while (i < tokens.Length)
        {
            var status = tokens[i];
            // Skip empty tokens (e.g. the trailing segment after the final
            // NUL).  A real status token is always non-empty.
            if (status.Length == 0) { i++; continue; }

            var c0 = status[0];
            if ((c0 == 'R' || c0 == 'C') && i + 2 < tokens.Length)
            {
                // Rename/copy: status, old, new (three tokens, no TAB).
                var isCopy = c0 == 'C';
                var oldName = tokens[i + 1];
                var newName = tokens[i + 2];
                if (oldName.Length > 0) target.Add(oldName);
                if (newName.Length > 0) target.Add(newName);
                // Only TRUE renames feed the rename-exclusion mechanism.  A
                // copy leaves its source intact, so its destination is a plain
                // staged addition that must be reverted/deleted (handled by
                // its presence in target), NOT protected as a rename endpoint
                // (B-1).
                if (!isCopy && oldName.Length > 0 && newName.Length > 0)
                    renamePairs.Add((oldName, newName));
                i += 3;
            }
            else
            {
                // Plain change: status, path (two tokens).
                if (i + 1 < tokens.Length)
                {
                    var path = tokens[i + 1];
                    if (path.Length > 0) target.Add(path);
                }
                i += 2;
            }
        }
    }
}
