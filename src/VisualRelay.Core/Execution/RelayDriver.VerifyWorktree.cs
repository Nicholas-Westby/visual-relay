using VisualRelay.Domain;

namespace VisualRelay.Core.Execution;

public sealed partial class RelayDriver
{
    /// <summary>
    /// Runs the authoritative gate (via <see cref="RunTestCommandWithRetryAsync"/>)
    /// against an ISOLATED, FULL-FIDELITY snapshot of <paramref name="rootPath"/> =
    /// committed HEAD + ALL uncommitted changes (tracked mods AND untracked-not-
    /// ignored). The suite may write freely in the snapshot; the real repo is never
    /// polluted. Returns the gate result and the DELTA — the files the TEST RUN wrote
    /// (captured before vs after the suite ran), NOT the agent's own edits.
    /// If <paramref name="rootPath"/> is not a git repo (or worktree creation fails),
    /// falls back to running against <paramref name="rootPath"/> directly with an
    /// empty delta (preserves today's behavior for non-git test fixtures).
    /// </summary>
    private async Task<(TestRunResult Result, IReadOnlyList<string> Mutations)> RunIsolatedVerifyAsync(
        string rootPath, RelayConfig config, int stageNumber, int attempt,
        string runId, string taskId, CancellationToken cancellationToken)
    {
        string? worktreePath;
        var worktreeId = $"{taskId}-verify-s{stageNumber}-a{attempt}";
        try
        {
            worktreePath = await CreateVerifyWorktreeAsync(rootPath, worktreeId, runId, cancellationToken);
        }
        catch
        {
            worktreePath = null; // non-git fixture or transient git failure → no isolation
        }

        if (worktreePath is null)
        {
            var inPlace = await RunTestCommandWithRetryAsync(rootPath, config, cancellationToken, stageNumber, runId, taskId);
            return (inPlace, Array.Empty<string>());
        }

        try
        {
            // Dirty set IMMEDIATELY AFTER the overlay / BEFORE the suite runs.
            var before = await CaptureDirtySetAsync(worktreePath, cancellationToken);
            var result = await RunTestCommandWithRetryAsync(worktreePath, config, cancellationToken, stageNumber, runId, taskId);
            // Dirty set AFTER the suite ran — the DELTA is the suite's writes.
            var after = await CaptureDirtySetAsync(worktreePath, cancellationToken);
            var mutations = after.Where(p => !before.Contains(p))
                                 .OrderBy(p => p, StringComparer.Ordinal)
                                 .ToList();
            return (result, mutations);
        }
        finally
        {
            await CleanupVerifyWorktreeAsync(rootPath, worktreePath, cancellationToken);
        }
    }

    /// <summary>
    /// TEST SEAM: drives the otherwise-private <see cref="CreateVerifyWorktreeAsync"/>
    /// so the verify-worktree copy/symlink overlay can be exercised directly. Production
    /// goes through <see cref="RunIsolatedVerifyAsync"/>; tests use this to avoid
    /// making the private surface public. <paramref name="thresholdBytes"/> lets a test
    /// inject a LOW copy/symlink boundary so it need not write 64 MB to exercise the
    /// large-entry (symlink) branch.
    /// </summary>
    internal Task<string> CreateVerifyWorktreeForTestAsync(
        string sourcePath, string worktreeId, string runId, CancellationToken cancellationToken,
        long thresholdBytes = IgnoredOverlayCopyMaxBytes) =>
        CreateVerifyWorktreeAsync(sourcePath, worktreeId, runId, cancellationToken, thresholdBytes);

    /// <summary>TEST SEAM: drives the private <see cref="CleanupVerifyWorktreeAsync"/>.</summary>
    internal Task CleanupVerifyWorktreeForTestAsync(
        string sourcePath, string worktreePath, CancellationToken cancellationToken) =>
        CleanupVerifyWorktreeAsync(sourcePath, worktreePath, cancellationToken);

    /// <summary>
    /// Creates a detached HEAD worktree (reusing <see cref="PlanningWorktree.CreateAsync"/>)
    /// then OVERLAYS the full uncommitted state of <paramref name="sourcePath"/> onto it:
    /// every tracked-modified and untracked-not-ignored file is copied across, so the
    /// snapshot mirrors exactly what the agent produced (Defect C). Throws if
    /// <paramref name="sourcePath"/> is not a git repo (caller catches → fallback).
    /// </summary>
    private async Task<string> CreateVerifyWorktreeAsync(
        string sourcePath, string worktreeId, string runId, CancellationToken cancellationToken,
        long thresholdBytes = IgnoredOverlayCopyMaxBytes)
    {
        var worktreePath = await PlanningWorktree.CreateAsync(
            sourcePath, worktreeId, runId, cancellationToken, _dependencies.GitInvoker);

        // (1) ADD / MODIFY — copy every tracked-modified and untracked-not-ignored file
        // across so the snapshot mirrors the agent's working tree. A path that `git diff`
        // reports but that no longer exists on disk is a DELETION: it is skipped here
        // (this loop only writes content that exists) and applied by step (2) below.
        foreach (var relative in await EnumerateUncommittedAsync(sourcePath, cancellationToken))
        {
            var src = Path.Combine(sourcePath, relative);
            if (!File.Exists(src)) continue;
            var dst = Path.Combine(worktreePath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }

        // (2) DELETE — the HEAD checkout resurrects every tracked file the agent removed,
        // so the snapshot would silently revert the deletion and verify a tree that still
        // contains the file (recompiling/retesting code the task intentionally removed).
        // Drop each deleted tracked path (and any parent dir it leaves empty) so the
        // snapshot reflects the deletion. Keys purely on git status — no project specifics.
        foreach (var relative in await EnumerateDeletedTrackedAsync(sourcePath, cancellationToken))
            RemoveDeletedOverlayPath(worktreePath, relative);

        // The overlay above carries only uncommitted-NOT-ignored files, so the
        // snapshot still omits everything git ignores — node_modules, .env, .venv,
        // dist, … — and the project's test command then can't resolve its deps or
        // read config, failing EVERY test on import. Mirror the source's git-ignored
        // RUNTIME content into the worktree per top-level entry:
        //   • SMALL entries (< threshold) are COPIED — real, writable, isolated files
        //     and dirs, so a test that WRITES a git-ignored path (e.g. TEST-TIMING.md,
        //     .test-tmp/) stays inside the sandboxed cwd instead of following a symlink
        //     OUT to the source (which nono --allow-cwd refuses → EPERM, failing the
        //     test), and never mutates the source.
        //   • LARGE entries (>= threshold, e.g. node_modules) are SYMLINKED — copying
        //     hundreds of MB per verify attempt is wasteful and these are read-mostly.
        // Cleanup unlinks the symlinks first so neither git nor the recursive delete
        // ever follows a link into the real tree; copies are worktree-local reals.
        foreach (var (name, isDirectory) in await EnumerateTopLevelIgnoredEntriesAsync(sourcePath, cancellationToken))
        {
            var src = Path.Combine(sourcePath, name);
            var dst = Path.Combine(worktreePath, name);
            // Don't clobber the detached checkout or the uncommitted overlay.
            if (File.Exists(dst) || Directory.Exists(dst)) continue;
            try
            {
                OverlayIgnoredEntry(src, dst, isDirectory, thresholdBytes);
            }
            catch
            {
                // Best-effort: a failed overlay must NOT abort worktree creation.
            }
        }
        return worktreePath;
    }

    /// <summary>
    /// Copy/symlink boundary for an ignored overlay entry (64 MB). BELOW it an entry
    /// is COPIED (writable + isolated → test writes stay in the sandboxed cwd); AT/ABOVE
    /// it the entry is SYMLINKED (avoids copying huge read-mostly deps like node_modules).
    /// </summary>
    private const long IgnoredOverlayCopyMaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Overlays one top-level ignored entry from <paramref name="src"/> to
    /// <paramref name="dst"/>: COPY when below <paramref name="thresholdBytes"/>,
    /// otherwise SYMLINK. Directory size uses the early-exiting
    /// <see cref="NonoRollbackSkipDirs.DirectoryMeetsSizeThreshold"/> (never fully sizes
    /// a multi-GB tree); a single file uses its length.
    /// </summary>
    private static void OverlayIgnoredEntry(string src, string dst, bool isDirectory, long thresholdBytes)
    {
        if (isDirectory)
        {
            if (!Directory.Exists(src)) return;
            if (NonoRollbackSkipDirs.DirectoryMeetsSizeThreshold(src, thresholdBytes))
                Directory.CreateSymbolicLink(dst, src); // large dep → share via link
            else
                CopyDirectoryResilient(src, dst); // small → copy so writes stay isolated
        }
        else
        {
            if (!File.Exists(src)) return;
            if (new FileInfo(src).Length >= thresholdBytes)
                File.CreateSymbolicLink(dst, src);
            else
                File.Copy(src, dst, overwrite: false);
        }
    }

    /// <summary>VR/VCS-internal dirs the verify worktree manages itself — never symlinked.</summary>
    private static readonly IReadOnlySet<string> IgnoredOverlayExcludedNames =
        new HashSet<string>(StringComparer.Ordinal) { ".git", ".relay", ".relay-scratch", ".swival" };

    /// <summary>
    /// Top-level git-ignored dirs that are BUILD OUTPUT (regenerated by the build/test
    /// command), NOT runtime dependencies — OMITTED from the overlay so the worktree
    /// builds them FRESH at its own path. They are PATH-SENSITIVE: compilers bake the
    /// absolute build path into module caches / artifact databases (SwiftPM <c>.build</c>,
    /// Cargo <c>target</c>, Gradle <c>build</c>), so providing them at the differently-
    /// pathed verify worktree breaks the build — a COPY carries stale baked paths
    /// ("compiled with module cache path X, currently Y"), and a SYMLINK makes the same
    /// module reachable via two paths ("module … defined in both") the instant anything
    /// recompiles, with writes resolving OUT of the sandboxed cwd (readonly). A fresh
    /// build is path-consistent and writable under <c>--allow-cwd</c>. Dependency dirs
    /// (node_modules, .venv, vendor) are NOT here: they are not path-sensitive and the
    /// test command can't regenerate them, so they ARE overlaid. Universal build
    /// conventions, not project-specific; extend as new toolchains appear.
    /// </summary>
    private static readonly IReadOnlySet<string> BuildOutputOverlaySkipNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ".build",                                                      // SwiftPM
            "target",                                                      // Cargo, Maven, sbt
            "build", ".gradle",                                            // Gradle / CMake
            "dist", "out",                                                 // bundlers / general
            "bin", "obj",                                                  // .NET, C/C++
            "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache",  // Python
            ".next", ".nuxt", ".svelte-kit", ".turbo", ".parcel-cache",    // JS frameworks/caches
            "DerivedData",                                                 // Xcode
        };

    /// <summary>Tracked-modified + untracked-not-ignored repo-relative paths (NUL-safe).</summary>
    private async Task<IReadOnlyList<string>> EnumerateUncommittedAsync(string rootPath, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var diff = await _dependencies.GitInvoker.RunAsync(rootPath, new[] { "diff", "--name-only", "-z" }, cancellationToken);
        foreach (var p in SplitNul(diff.Output)) set.Add(p);
        var untracked = await _dependencies.GitInvoker.RunAsync(rootPath, new[] { "ls-files", "--others", "--exclude-standard", "-z" }, cancellationToken);
        foreach (var p in SplitNul(untracked.Output)) set.Add(p);
        return set.ToList();
    }

    /// <summary>The worktree's current dirty set (tracked mods + untracked), NUL-safe.</summary>
    private async Task<HashSet<string>> CaptureDirtySetAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var diff = await _dependencies.GitInvoker.RunAsync(worktreePath, new[] { "diff", "--name-only", "-z" }, cancellationToken);
        foreach (var p in SplitNul(diff.Output)) set.Add(p);
        var untracked = await _dependencies.GitInvoker.RunAsync(worktreePath, new[] { "ls-files", "--others", "--exclude-standard", "-z" }, cancellationToken);
        foreach (var p in SplitNul(untracked.Output)) set.Add(p);
        return set;
    }

    private static IEnumerable<string> SplitNul(string? gitOutput) =>
        (gitOutput ?? string.Empty).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Best-effort teardown: git worktree remove, then a resilient dir delete.</summary>
    private async Task CleanupVerifyWorktreeAsync(string sourcePath, string worktreePath, CancellationToken cancellationToken)
    {
        // SAFETY-CRITICAL: unlink the symlinks we added FIRST. A `git worktree remove`
        // or a recursive Directory.Delete that traversed a DIRECTORY symlink would
        // delete the REAL node_modules/.env contents in the source repo. Remove the
        // LINKS only (never recursive on a reparse point) so nothing can follow them.
        UnlinkOverlaySymlinks(worktreePath);
        await PlanningWorktree.RemoveAsync(sourcePath, worktreePath, cancellationToken, _dependencies.GitInvoker);
        try { if (Directory.Exists(worktreePath)) Directory.Delete(worktreePath, recursive: true); }
        catch { /* PRODUCTION fallback — never reference TestFileSystem here (Defect E). */ }
    }

    /// <summary>
    /// Removes the top-level symlinks added by the ignored-content overlay, leaving their
    /// TARGETS untouched. For a directory symlink, <c>Directory.Delete(recursive:false)</c>
    /// removes the link only; passing recursive:true on a reparse point would delete the
    /// target's contents. Best-effort per entry — never throws.
    /// </summary>
    private static void UnlinkOverlaySymlinks(string worktreePath)
    {
        if (!Directory.Exists(worktreePath)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(worktreePath))
        {
            try
            {
                var attributes = File.GetAttributes(entry);
                if (!attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (attributes.HasFlag(FileAttributes.Directory))
                    Directory.Delete(entry, recursive: false); // unlink dir symlink, never its target
                else
                    File.Delete(entry); // unlink file symlink
            }
            catch
            {
                // Best-effort: leave it for git worktree remove / the dir delete fallback.
            }
        }
    }

    /// <summary>
    /// Emits a <c>verify_mutated_tree</c> warn advisory naming the DELTA files the test
    /// command wrote during verify. Emitted only when the delta is non-empty; the real
    /// <paramref name="rootPath"/> tree is always unaffected (the gate ran in the snapshot).
    /// </summary>
    // NOTE: isolation covers the authoritative test gates (stages 9/10) only — the bootstrap
    // check and commit gate still run in-place — so "the repo is unaffected" in the advisory
    // refers specifically to the test command's writes during those isolated gate runs.
    private async Task EmitMutatedTreeAdvisoryAsync(
        string rootPath, string runId, string taskId, RelayStageDefinition stage,
        IReadOnlyList<string> mutations, CancellationToken cancellationToken)
    {
        if (mutations.Count == 0) return;
        await _dependencies.EventSink.PublishAsync(new RelayEvent(
            DateTimeOffset.UtcNow, "warn", "verify_mutated_tree", runId, rootPath, taskId,
            stage.Number, stage.Tier,
            Data: new Dictionary<string, string>
            {
                ["files"] = string.Join(' ', mutations),
                ["advice"] = "the test command wrote these files during verify; VR ran the gate in an "
                           + "isolated tree so the repo is unaffected — gitignore them or use a non-writing "
                           + "test command for idempotent verification"
            }), cancellationToken);
    }
}
