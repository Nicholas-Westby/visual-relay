using System.Text.Json;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Durable snapshot + restore of a flagged working tree, stored as a git bundle
/// under .relay/&lt;taskId&gt;/flagged-work.bundle (auto-ignored by .relay/* gitignore).
/// All git writes are harness-side plumbing through <see cref="IGitInvoker"/> —
/// no agent commits or hook bypass.
/// </summary>
internal static partial class FlaggedWorkStore
{
    internal const string BundleFileName = "flagged-work.bundle";
    private const string SidecarFileName = "flagged-work.json";

    /// <summary>
    /// Best-effort: snapshots the working tree as a git bundle. Never throws;
    /// failures are silently logged and the caller continues with the flag.
    /// </summary>
    internal static async Task CaptureAsync(
        string rootPath,
        string taskId,
        string taskDirectory,
        int flaggedStage,
        IGitInvoker gitInvoker,
        DateTimeOffset createdUtc,
        CancellationToken ct)
    {
        try
        {
            var runBasePath = Path.Combine(taskDirectory, "run-base.txt");
            if (!File.Exists(runBasePath))
                return;
            var runBaseSha = (await File.ReadAllTextAsync(runBasePath, ct)).Trim();
            if (string.IsNullOrEmpty(runBaseSha))
                return;

            // Read pre-run untracked snapshot so we can exclude pre-existing untracked files.
            var preRunUntrackedPath = Path.Combine(taskDirectory, "pre-run-untracked.txt");
            IReadOnlySet<string> preRunUntracked = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(preRunUntrackedPath))
            {
                var lines = await File.ReadAllLinesAsync(preRunUntrackedPath, ct);
                preRunUntracked = new HashSet<string>(lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()), StringComparer.Ordinal);
            }

            // Create a temporary index file. Use a directory under root so it's on the
            // same filesystem (avoids cross-device rename issues with some git commands).
            var tempIndex = Path.Combine(Path.GetTempPath(), $"git-index-{Guid.NewGuid():N}");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GIT_INDEX_FILE"] = tempIndex
                };

                // Seed the temp index from the run base so tracked files carry
                // their base modes through the subsequent `git add -A`, preventing
                // loss of executable bits under core.fileMode=false.
                var readTreeResult = await gitInvoker.RunAsync(
                    rootPath, ["read-tree", runBaseSha], ct, environment: env);
                if (readTreeResult.ExitCode != 0)
                    return;

                // Stage everything (tracked edits + untracked files) into the temp
                // index. Force core.fileMode=true so on-disk executable bits are
                // honored regardless of the repo config.
                var addResult = await gitInvoker.RunAsync(
                    rootPath, ["-c", "core.fileMode=true", "add", "-A"], ct, environment: env);
                if (addResult.ExitCode != 0)
                    return;

                // Reset .relay/ paths in the temp index back to the run base's
                // versions.  Tracked-at-base files (config.json, .gitignore) are
                // preserved exactly; runtime metadata (git-ignored, never in base)
                // is removed from the index.  This replaces the old `git rm --cached`
                // which stripped ALL .relay/ entries including tracked ones, recording
                // them as deletions in the snapshot tree.
                _ = await gitInvoker.RunAsync(
                    rootPath, ["restore", "--source", runBaseSha, "--staged", "--", ".relay/"], ct, environment: env);

                // Unstage pre-existing untracked files (they were not authored by this task).
                foreach (var path in preRunUntracked)
                {
                    // Best-effort: ignore failures.
                    _ = await gitInvoker.RunAsync(
                        rootPath, ["rm", "--cached", "-q", "--", path], ct, environment: env);
                }

                // Write the snapshot tree.
                var treeResult = await gitInvoker.RunAsync(rootPath, ["write-tree"], ct, environment: env);
                if (treeResult.ExitCode != 0 || string.IsNullOrWhiteSpace(treeResult.Output))
                    return;
                var treeSha = treeResult.Output.Trim();

                // Create a snapshot commit parented on the run base.
                var commitResult = await gitInvoker.RunAsync(
                    rootPath,
                    ["commit-tree", treeSha, "-p", runBaseSha, "-m", $"flagged-work snapshot stage {flaggedStage}"],
                    ct,
                    environment: env);
                if (commitResult.ExitCode != 0 || string.IsNullOrWhiteSpace(commitResult.Output))
                    return;
                var snapshotSha = commitResult.Output.Trim();

                // Create the bundle. git >= 2.54 requires ref names (not bare SHAs) in
                // `git bundle create`. Use a temporary ref.
                var snapshotRef = $"refs/relay-snapshot/{taskId}";
                await gitInvoker.RunAsync(rootPath, ["update-ref", snapshotRef, snapshotSha], ct);
                try
                {
                    var bundlePath = Path.Combine(taskDirectory, BundleFileName);
                    var bundleResult = await gitInvoker.RunAsync(
                        rootPath,
                        ["bundle", "create", bundlePath, snapshotRef, $"^{runBaseSha}"],
                        ct);
                    if (bundleResult.ExitCode != 0)
                        return;
                }
                finally
                {
                    _ = await gitInvoker.RunAsync(rootPath, ["update-ref", "-d", snapshotRef], ct, killToken: CancellationToken.None);
                }

                // Write sidecar.
                var sidecarPath = Path.Combine(taskDirectory, SidecarFileName);
                var sidecar = JsonSerializer.Serialize(
                    new FlaggedWorkSidecar(runBaseSha, createdUtc, flaggedStage),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                await File.WriteAllTextAsync(sidecarPath, sidecar, ct);
            }
            finally
            {
                try { File.Delete(tempIndex); } catch { /* best-effort */ }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Best-effort: never let snapshot failure block the flag.
        }
    }

    /// <summary>
    /// Restores the flagged work from the bundle onto the current working tree.
    /// Returns a <see cref="RestoreResult"/> describing the outcome.
    /// </summary>
    internal static async Task<RestoreResult> RestoreAsync(
        string rootPath,
        string taskId,
        string taskDirectory,
        IGitInvoker gitInvoker,
        CancellationToken ct)
    {
        var bundlePath = Path.Combine(taskDirectory, BundleFileName);
        if (!File.Exists(bundlePath))
            return RestoreResult.Unrestorable;

        // Verify the bundle.
        var verifyResult = await gitInvoker.RunAsync(rootPath, ["bundle", "verify", bundlePath], ct);
        if (verifyResult.ExitCode != 0)
            return RestoreResult.Unrestorable;

        // Fetch the snapshot commit from the bundle. The bundle records the snapshot
        // under refs/relay-snapshot/<taskId>.
        var snapshotRef = $"refs/relay-snapshot/{taskId}";
        var fetchRef = $"refs/relay-resume/{taskId}";
        var fetchResult = await gitInvoker.RunAsync(
            rootPath, ["fetch", bundlePath, $"+{snapshotRef}:{fetchRef}"], ct);
        if (fetchResult.ExitCode != 0)
            return RestoreResult.Unrestorable;

        try
        {
            // Resolve the fetched commit SHA.
            var revParseResult = await gitInvoker.RunAsync(rootPath, ["rev-parse", fetchRef], ct);
            if (revParseResult.ExitCode != 0 || string.IsNullOrWhiteSpace(revParseResult.Output))
                return RestoreResult.Unrestorable;
            var snapshotSha = revParseResult.Output.Trim();

            // Check whether .relay/config.json is tracked at HEAD so we can
            // guard against its deletion after the cherry-pick (defense-in-depth
            // against legacy lossy bundles).
            var configPath = Path.Combine(rootPath, ".relay", "config.json");
            var configTrackedAtHead = false;
            if (File.Exists(configPath))
            {
                var lsTreeResult = await gitInvoker.RunAsync(
                    rootPath, ["ls-tree", "HEAD", "--", ".relay/config.json"], ct);
                configTrackedAtHead = lsTreeResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(lsTreeResult.Output);
            }

            // 3-way apply via cherry-pick -n.
            var cherryResult = await gitInvoker.RunAsync(
                rootPath, ["cherry-pick", "-n", snapshotSha], ct);
            // Always clear sequencer state (keep working-tree changes even on conflict).
            _ = await gitInvoker.RunAsync(rootPath, ["cherry-pick", "--quit"], ct);

            // Guard: if .relay/config.json was tracked at HEAD but the
            // cherry-pick deleted it (a lossy legacy bundle), bail out loudly
            // instead of proceeding into a broken run.
            if (configTrackedAtHead && !File.Exists(configPath))
                return RestoreResult.Unrestorable;

            // Treat a successful cherry-pick (exit code 0) as a clean apply.
            if (cherryResult.ExitCode != 0)
            {
                var unmergedResult = await gitInvoker.RunAsync(
                    rootPath, ["-c", "core.quotePath=false", "ls-files", "-u"], ct);
                var conflictedFiles = unmergedResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(unmergedResult.Output)
                    ? GitPathOutput.ParseLines(unmergedResult.Output)
                        .Select(line =>
                        {
                            // git ls-files -u output format: <mode> <sha> <stage>\t<path>
                            var tab = line.IndexOf('\t');
                            return tab >= 0 ? line[(tab + 1)..] : line;
                        })
                        .Distinct(StringComparer.Ordinal)
                        .ToList()
                    : new List<string>();

                return conflictedFiles.Count > 0
                    ? RestoreResult.Conflicts(conflictedFiles)
                    : RestoreResult.Success;
            }

            return RestoreResult.Success;
        }
        finally
        {
            // Clean up the fetch ref.
            _ = await gitInvoker.RunAsync(
                rootPath, ["update-ref", "-d", fetchRef], ct, killToken: CancellationToken.None);
        }
    }

    /// <summary>
    /// Deletes the flagged-work bundle and its sidecar. No-op if they don't exist.
    /// </summary>
    internal static void Delete(string taskDirectory)
    {
        try
        {
            var bundlePath = Path.Combine(taskDirectory, BundleFileName);
            if (File.Exists(bundlePath))
                File.Delete(bundlePath);
            var sidecarPath = Path.Combine(taskDirectory, SidecarFileName);
            if (File.Exists(sidecarPath))
                File.Delete(sidecarPath);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // Serialized to flagged-work.json as the resume sidecar (tests assert the file is
    // written). The fields are persisted via JSON reflection, which the analyzer cannot
    // see, so they read as "never accessed" — hence the scoped suppression.
    // ReSharper disable NotAccessedPositionalProperty.Global
    internal sealed record FlaggedWorkSidecar(
        string BaseSha,
        DateTimeOffset CreatedUtc,
        int FlaggedStage);
    // ReSharper restore NotAccessedPositionalProperty.Global

    internal sealed record RestoreResult
    {
        public bool IsSuccess { get; private init; }
        public bool IsUnrestorable { get; private init; }
        public bool HasConflicts { get; private init; }
        public IReadOnlyList<string> ConflictedFiles { get; private init; } = [];

        public static RestoreResult Success { get; } = new() { IsSuccess = true };
        public static RestoreResult Unrestorable { get; } = new() { IsUnrestorable = true };
        public static RestoreResult Conflicts(IReadOnlyList<string> files) => new()
        {
            HasConflicts = true,
            ConflictedFiles = files
        };
    }
}
