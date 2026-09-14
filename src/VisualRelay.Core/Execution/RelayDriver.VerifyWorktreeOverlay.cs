using VisualRelay.Core.Execution.Wsl;
using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

// Mirroring a source tree's git-ignored runtime content into a throwaway worktree.
public sealed partial class RelayDriver
{
    /// <summary>
    /// Copies (or clones, or symlinks) every git-ignored RUNTIME entry of
    /// <paramref name="sourcePath"/> into <paramref name="worktreePath"/>, so a command
    /// run in the worktree can resolve its dependencies and read its config. Shared by
    /// the verify snapshot and the pristine-base checkout the guard attribution probe
    /// runs in. Best-effort per entry: a failure warns and moves on. Returns the entries it
    /// enumerated, so a caller can find what the snapshot inherited (a virtualenv, say).
    /// </summary>
    private async Task<IReadOnlyList<(string Name, bool IsDirectory)>> OverlayIgnoredEntriesAsync(
        string sourcePath, string worktreePath, string worktreeId, string runId,
        long thresholdBytes, bool cloneOverlay, CancellationToken cancellationToken)
    {
        // A checkout plus a not-ignored overlay still omits everything git ignores —
        // node_modules, .env, .venv, dist, … — and the project's test command then
        // can't resolve its deps or
        // read config, failing EVERY test on import. Mirror the source's git-ignored
        // RUNTIME content into the worktree per entry — TOP-LEVEL and NESTED (a pnpm
        // workspace's packages/*/node_modules is nested; dropping it broke per-package
        // resolution, e.g. tsc's TS2688 on types:["node"]):
        //   • CLONE first (APFS copy-on-write, macOS): the whole entry becomes REAL
        //     and WRITABLE at near-zero cost, so test-time writes AND unlinks stay in
        //     the sandboxed cwd and realpath-based resolution never leaves the
        //     snapshot. Unavailable (non-APFS/cross-volume/other OS) → fall back to:
        //   • SMALL entries (< threshold) are COPIED — real, writable, isolated files
        //     and dirs, so a test that WRITES a git-ignored path (e.g. TEST-TIMING.md,
        //     .test-tmp/) stays inside the sandboxed cwd instead of following a symlink
        //     OUT to the source (which nono --allow-cwd refuses → EPERM, failing the
        //     test), and never mutates the source.
        //   • LARGE entries (>= threshold, e.g. node_modules) are SYMLINKED — copying
        //     hundreds of MB per verify attempt is wasteful and these are read-mostly.
        // Cleanup unlinks the symlinks first so neither git nor the recursive delete
        // ever follows a link into the real tree; copies and clones are worktree-local
        // reals.
        var entries = await EnumerateOverlayIgnoredEntriesAsync(sourcePath, cancellationToken);
        // Inside the distro on the Windows arm: the app's copies there lose executable modes
        // and its links point at Windows paths (WslTreeCopy says more).
        if (await TryCopyInDistroAsync(sourcePath, worktreePath, worktreeId, runId,
                (context, source, dest) => entries.Count == 0
                    ? null
                    : WslTreeCopy.IgnoredEntries(context, source, dest, thresholdBytes, entries.Select(e => e.Name).ToList()),
                cancellationToken))
            return entries;

        foreach (var (name, isDirectory) in entries)
        {
            var src = Path.Combine(sourcePath, name);
            var dst = Path.Combine(worktreePath, name);
            // Don't clobber the detached checkout or the uncommitted overlay.
            if (File.Exists(dst) || Directory.Exists(dst)) continue;
            try
            {
                // A nested entry's parents are tracked dirs already present in the
                // checkout; idempotent for top-level entries.
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

                if (cloneOverlay && TryCloneOverlayEntry(src, dst, sourcePath, worktreePath))
                    continue;

                if (isDirectory)
                {
                    long copiedBytes = 0;
                    OverlayIgnoredDirRecursive(
                        src, dst, thresholdBytes, depth: 0, ref copiedBytes,
                        runId, sourcePath, worktreeId);
                }
                else
                {
                    OverlayIgnoredFile(src, dst, thresholdBytes);
                }
            }
            catch (Exception ex)
            {
                // Best-effort: a failed overlay must NOT abort worktree creation.
                try
                {
                    await _dependencies.EventSink.PublishAsync(new RelayEvent(
                        DateTimeOffset.UtcNow, "warn", "verify_overlay_skipped", runId, sourcePath, worktreeId,
                        Data: new Dictionary<string, string>
                        {
                            ["entry"] = name,
                            ["error"] = ex.Message
                        }), cancellationToken);
                }
                catch
                {
                    // Publish itself must not throw — the overlay loop must always complete.
                }
            }
        }

        return entries;
    }
}
