using System.Runtime.InteropServices;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Overlays one ignored entry as an APFS copy-on-write clone: every file in the
    /// snapshot is REAL and WRITABLE (a ~1GB store clones in seconds at near-zero disk
    /// cost), so test-time writes/unlinks stay inside the sandboxed worktree cwd and
    /// module resolution (realpath) never escapes into the source repo — the failure
    /// class a whole-dir symlink has for any toolchain that writes inside its dep tree
    /// (vitest tsbuildinfo in .pnpm, vite dep caches). Returns <c>false</c> — after
    /// removing any partial result — whenever cloning is unavailable (non-macOS,
    /// cross-volume EXDEV, non-APFS ENOTSUP, any errno); the caller then falls back to
    /// the recursive copy/symlink machinery. Never throws.
    /// </summary>
    private static bool TryCloneOverlayEntry(string src, string dst, string sourcePath, string worktreePath)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            if (CloneFile(src, dst, 0) != 0)
            {
                RemoveCloneDebris(dst); // clonefile may leave a partial hierarchy on error
                return false;
            }
            // clonefile copies symlink targets VERBATIM: an ABSOLUTE link into the
            // source repo would let a write through it escape the sandbox cwd, so
            // rewrite those to the equivalent worktree path (relative links already
            // resolve worktree-locally; absolute links elsewhere stay shared).
            RewriteClonedAbsoluteInternalLinks(dst, Path.GetFullPath(sourcePath), Path.GetFullPath(worktreePath));
            return true;
        }
        catch
        {
            RemoveCloneDebris(dst);
            return false;
        }
    }

    /// <summary>
    /// Removes whatever a failed clone left at <paramref name="dst"/> without ever
    /// following a symlink (reuses the teardown's link-safe unlink walk before the
    /// recursive delete). Best-effort — never throws.
    /// </summary>
    private static void RemoveCloneDebris(string dst)
    {
        try
        {
            if (File.Exists(dst))
            {
                File.Delete(dst);
                return;
            }
            if (!Directory.Exists(dst)) return;
            if (new DirectoryInfo(dst).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(dst, recursive: false); // unlink the link node only
                return;
            }
            UnlinkOverlaySymlinks(dst);
            Directory.Delete(dst, recursive: true);
        }
        catch
        {
            // Leftovers are inside the ephemeral worktree — teardown removes them.
        }
    }

    /// <summary>
    /// Post-clone fixup: walks the cloned tree (REAL directories only — reparse points
    /// are never traversed) and re-targets every ABSOLUTE symlink that points inside
    /// <paramref name="sourceRoot"/> to the corresponding path under
    /// <paramref name="destRoot"/>. Per-entry best-effort, mirroring the overlay walk's
    /// resilience.
    /// </summary>
    private static void RewriteClonedAbsoluteInternalLinks(string cloneRoot, string sourceRoot, string destRoot)
    {
        var pending = new Stack<string>();
        pending.Push(cloneRoot);
        while (pending.Count > 0)
        {
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos(); }
            catch { continue; }
            foreach (var entry in entries)
            {
                try
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        RewriteLinkIfAbsoluteInternal(entry, sourceRoot, destRoot);
                    else if (entry is DirectoryInfo)
                        pending.Push(entry.FullName);
                }
                catch
                {
                    // Per-entry IO error — skip it, never abort the fixup walk.
                }
            }
        }
    }

    /// <summary>
    /// Replaces one symlink NODE whose absolute target lies inside
    /// <paramref name="sourceRoot"/> with a link to the prefix-swapped
    /// <paramref name="destRoot"/> path (same rule as <see cref="RecreateSymlink"/>).
    /// Relative, external-absolute, and unreadable targets are left untouched.
    /// </summary>
    private static void RewriteLinkIfAbsoluteInternal(FileSystemInfo link, string sourceRoot, string destRoot)
    {
        string? target;
        try { target = link.LinkTarget; }
        catch { return; }
        if (string.IsNullOrEmpty(target) || !Path.IsPathRooted(target)) return;

        var normalized = Path.GetFullPath(target);
        if (normalized != sourceRoot
            && !normalized.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return;

        var rewritten = normalized == sourceRoot
            ? destRoot
            : Path.Combine(destRoot, normalized[(sourceRoot.Length + 1)..]);

        // Swap the NODE, never touching what it points at.
        if (link is DirectoryInfo)
        {
            Directory.Delete(link.FullName, recursive: false);
            Directory.CreateSymbolicLink(link.FullName, rewritten);
        }
        else
        {
            File.Delete(link.FullName);
            File.CreateSymbolicLink(link.FullName, rewritten);
        }
    }

    /// <summary>
    /// <c>clonefile(2)</c>: copy-on-write clone of a file or an entire directory
    /// hierarchy on APFS. Fails honestly (EXDEV/ENOTSUP/EEXIST/…) where cloning is
    /// impossible — unlike <c>cp -c</c>, which silently degrades to a full copy.
    /// flags=0 follows a symlink given AS <paramref name="src"/> (matching the copy
    /// fallback's top-entry semantics) while still cloning links INSIDE the hierarchy
    /// as links.
    /// </summary>
    [DllImport("libSystem.dylib", EntryPoint = "clonefile", SetLastError = true)]
    private static extern int CloneFile(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string src,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dst,
        uint flags);
}
