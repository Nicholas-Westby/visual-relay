using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Maximum recursion depth for the per-entry overlay walk. A tree deeper than
    /// this triggers a fallback whole-subtree symlink so pathological nesting can't
    /// make overlay unbounded.
    /// </summary>
    private const int MaxOverlayRecursionDepth = 16;

    /// <summary>
    /// Overlays one top-level ignored FILE from <paramref name="src"/> to
    /// <paramref name="dst"/>: COPY when below <paramref name="thresholdBytes"/>,
    /// otherwise SYMLINK. Directories go through the recursive
    /// <see cref="OverlayIgnoredDirRecursive"/> walk instead.
    /// </summary>
    private static void OverlayIgnoredFile(string src, string dst, long thresholdBytes)
    {
        if (!File.Exists(src)) return;
        if (new FileInfo(src).Length >= thresholdBytes)
            File.CreateSymbolicLink(dst, src);
        else
            File.Copy(src, dst, overwrite: false);
    }

    /// <summary>
    /// Recursively overlays one git-ignored directory tree from <paramref name="srcDir"/>
    /// into <paramref name="dstDir"/>, evaluating each entry individually:
    ///   • Symlink entry → recreate the link node (never follow/traverse).
    ///   • Directory at/above <paramref name="thresholdBytes"/> → whole-dir symlink
    ///     (read-mostly bulk shared cheaply).
    ///   • Directory below threshold → create REAL dir and recurse (writable, isolated).
    ///   • File → copy (or symlink if individual file ≥ threshold).
    ///
    /// Bounded by <paramref name="depth"/> (<see cref="MaxOverlayRecursionDepth"/>)
    /// and per-top-level-entry <paramref name="copiedBytes"/> budget; on hitting either
    /// bound the remaining subtree is symlinked and a <c>verify_overlay_skipped</c>
    /// warn event is emitted. Directory sizing uses the early-exiting
    /// <see cref="NonoRollbackSkipDirs.DirectoryMeetsSizeThreshold"/> (never fully
    /// sizes a huge tree). The walk is resilient per entry — errors are swallowed,
    /// never aborting worktree creation — and never follows a reparse point during
    /// traversal.
    /// </summary>
    private void OverlayIgnoredDirRecursive(
        string srcDir, string dstDir, long thresholdBytes,
        int depth, ref long copiedBytes,
        string runId, string sourcePath, string worktreeId)
    {
        // --- bounds -----------------------------------------------------------
        if (depth > MaxOverlayRecursionDepth)
        {
            try { Directory.CreateSymbolicLink(dstDir, srcDir); } catch { /* best-effort fallback symlink — the skip advisory below still fires */ }
            EmitOverlaySkipAdvisory(runId, sourcePath, worktreeId,
                Path.GetFileName(srcDir), "max_depth_exceeded");
            return;
        }

        if (depth > 0 && copiedBytes >= thresholdBytes)
        {
            try { Directory.CreateSymbolicLink(dstDir, srcDir); } catch { /* best-effort fallback symlink — the skip advisory below still fires */ }
            EmitOverlaySkipAdvisory(runId, sourcePath, worktreeId,
                Path.GetFileName(srcDir), "copy_budget_exhausted");
            return;
        }

        // Depth > 0: if the dir itself is large, share it as a whole-dir symlink
        // (normal large-child path — no event, this is expected).
        if (depth > 0 && NonoRollbackSkipDirs.DirectoryMeetsSizeThreshold(srcDir, thresholdBytes))
        {
            try { Directory.CreateSymbolicLink(dstDir, srcDir); } catch { /* best-effort share of a large child — skipping it never aborts the walk */ }
            return;
        }

        // --- create the real destination directory -----------------------------
        try { Directory.CreateDirectory(dstDir); } catch { return; }

        // --- enumerate children ------------------------------------------------
        DirectoryInfo src;
        try
        {
            src = new DirectoryInfo(srcDir);
            if (!src.Exists) return;
        }
        catch { return; }

        IEnumerable<FileSystemInfo> entries;
        try { entries = src.EnumerateFileSystemInfos(); }
        catch { return; }

        foreach (var entry in entries)
        {
            try
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    RecreateSymlink(entry, srcDir, dstDir);
                    continue;
                }

                if (entry is DirectoryInfo subDir)
                {
                    OverlayIgnoredDirRecursive(
                        subDir.FullName, Path.Combine(dstDir, entry.Name),
                        thresholdBytes, depth + 1, ref copiedBytes,
                        runId, sourcePath, worktreeId);
                }
                else if (entry is FileInfo file)
                {
                    var childDst = Path.Combine(dstDir, entry.Name);
                    if (file.Length >= thresholdBytes)
                        File.CreateSymbolicLink(childDst, file.FullName);
                    else
                    {
                        file.CopyTo(childDst, overwrite: false);
                        copiedBytes += file.Length;
                    }
                }
            }
            catch
            {
                // Per-entry IO error — skip it, never abort the walk.
            }
        }
    }

    /// <summary>
    /// Fire-and-forget best-effort publish of a <c>verify_overlay_skipped</c>
    /// warn event. A failure to publish must never propagate.
    /// </summary>
    private void EmitOverlaySkipAdvisory(
        string runId, string sourcePath, string worktreeId, string entry, string reason)
    {
        try
        {
            _ = _dependencies.EventSink.PublishAsync(new RelayEvent(
                DateTimeOffset.UtcNow, "warn", "verify_overlay_skipped", runId, sourcePath, worktreeId,
                Data: new Dictionary<string, string>
                {
                    ["entry"] = entry,
                    ["reason"] = reason
                }), CancellationToken.None);
        }
        catch
        {
            // Best-effort only — never let event publishing break the overlay.
        }
    }
}
