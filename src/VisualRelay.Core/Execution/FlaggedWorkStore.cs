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
    /// Who the snapshot commit names. It is Visual Relay's own plumbing commit and only
    /// ever lives inside the bundle, so it must not need the user's identity: measured in
    /// a WSL distro whose git had none, commit-tree refused with "empty ident name not
    /// allowed" and the flagged work was lost. On the Windows arm these reach the git
    /// inside the distro as <c>env</c> arguments (<see cref="Wsl.GitRouting.Launch"/>).
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SnapshotIdentity =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_AUTHOR_NAME"] = "Visual Relay",
            ["GIT_AUTHOR_EMAIL"] = "visual-relay@localhost",
            ["GIT_COMMITTER_NAME"] = "Visual Relay",
            ["GIT_COMMITTER_EMAIL"] = "visual-relay@localhost"
        };

    /// <summary>
    /// Snapshots the working tree as a git bundle. Never throws except on cancellation.
    /// A failed step is reported with what git said rather than swallowed: the caller
    /// has to say so, because the working tree is then the only copy of the work.
    /// </summary>
    internal static async Task<CaptureResult> CaptureAsync(
        string rootPath,
        string taskId,
        string taskDirectory,
        int flaggedStage,
        IGitInvoker gitInvoker,
        DateTimeOffset createdUtc,
        CancellationToken ct)
    {
        // The step in flight, so a throw is reported against it like a refusal is.
        var step = "prepare";
        try
        {
            var runBasePath = Path.Combine(taskDirectory, "run-base.txt");
            if (!File.Exists(runBasePath))
                return CaptureResult.NothingToCapture;
            var runBaseSha = (await File.ReadAllTextAsync(runBasePath, ct)).Trim();
            if (string.IsNullOrEmpty(runBaseSha))
                return CaptureResult.NothingToCapture;

            // Read pre-run untracked snapshot so we can exclude pre-existing untracked files.
            var preRunUntrackedPath = Path.Combine(taskDirectory, "pre-run-untracked.txt");
            IReadOnlySet<string> preRunUntracked = new HashSet<string>(StringComparer.Ordinal);
            if (File.Exists(preRunUntrackedPath))
            {
                var lines = await File.ReadAllLinesAsync(preRunUntrackedPath, ct);
                preRunUntracked = new HashSet<string>(lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()), StringComparer.Ordinal);
            }

            // Create a temporary index file where the git that serves this workspace
            // can create it — inside the distro on the Windows arm, where a Windows
            // temp path is a relative name — and where VR can still delete it.
            var indexDirectory = WorktreeNamespace.TempIndexFor(rootPath, SandboxHost.Current);
            Directory.CreateDirectory(indexDirectory.Io);
            var tempIndex = indexDirectory.Child($"git-index-{Guid.NewGuid():N}");
            try
            {
                var env = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["GIT_INDEX_FILE"] = tempIndex.Git
                };

                // Seed the temp index from the run base so tracked files carry
                // their base modes through the subsequent `git add -A`, preventing
                // loss of executable bits under core.fileMode=false.
                step = "read-tree";
                var readTreeResult = await gitInvoker.RunAsync(
                    rootPath, ["read-tree", runBaseSha], ct, environment: env);
                if (readTreeResult.ExitCode != 0)
                    return CaptureResult.Failed(step, readTreeResult.Output);

                // Stage everything (tracked edits + untracked files) into the temp
                // index. Force core.fileMode=true so on-disk executable bits are
                // honored regardless of the repo config.
                step = "add";
                var addResult = await gitInvoker.RunAsync(
                    rootPath, ["-c", "core.fileMode=true", "add", "-A"], ct, environment: env);
                if (addResult.ExitCode != 0)
                    return CaptureResult.Failed(step, addResult.Output);

                // Reset .relay/ paths in the temp index back to the run base's
                // versions.  Tracked-at-base files (config.json, .gitignore) are
                // preserved exactly; runtime metadata (git-ignored, never in base)
                // is removed from the index.  This replaces the old `git rm --cached`
                // which stripped ALL .relay/ entries including tracked ones, recording
                // them as deletions in the snapshot tree.
                step = "restore";
                _ = await gitInvoker.RunAsync(
                    rootPath, ["restore", "--source", runBaseSha, "--staged", "--", ".relay/"], ct, environment: env);

                // Unstage pre-existing untracked files (they were not authored by this task).
                step = "rm";
                foreach (var path in preRunUntracked)
                {
                    // Best-effort: ignore failures.
                    _ = await gitInvoker.RunAsync(
                        rootPath, ["rm", "--cached", "-q", "--", path], ct, environment: env);
                }

                // Write the snapshot tree.
                step = "write-tree";
                var treeResult = await gitInvoker.RunAsync(rootPath, ["write-tree"], ct, environment: env);
                if (treeResult.ExitCode != 0 || string.IsNullOrWhiteSpace(treeResult.Output))
                    return CaptureResult.Failed(step, treeResult.Output);
                var treeSha = treeResult.Output.Trim();

                // Create a snapshot commit parented on the run base, under its own identity.
                step = "commit-tree";
                var commitResult = await gitInvoker.RunAsync(
                    rootPath,
                    ["commit-tree", treeSha, "-p", runBaseSha, "-m", $"flagged-work snapshot stage {flaggedStage}"],
                    ct,
                    environment: SnapshotIdentity);
                if (commitResult.ExitCode != 0 || string.IsNullOrWhiteSpace(commitResult.Output))
                    return CaptureResult.Failed(step, commitResult.Output);
                var snapshotSha = commitResult.Output.Trim();

                // Create the bundle. git >= 2.54 requires ref names (not bare SHAs) in
                // `git bundle create`. Use a temporary ref.
                step = "update-ref";
                var snapshotRef = $"refs/relay-snapshot/{taskId}";
                var updateRefResult = await gitInvoker.RunAsync(rootPath, ["update-ref", snapshotRef, snapshotSha], ct);
                if (updateRefResult.ExitCode != 0)
                    return CaptureResult.Failed(step, updateRefResult.Output);
                try
                {
                    step = "bundle";
                    var bundleArg = RepoRelativeBundle(rootPath, taskDirectory);
                    var bundleResult = await gitInvoker.RunAsync(
                        rootPath,
                        ["bundle", "create", bundleArg, snapshotRef, $"^{runBaseSha}"],
                        ct);
                    if (bundleResult.ExitCode != 0)
                        return CaptureResult.Failed(step, bundleResult.Output);
                }
                finally
                {
                    _ = await gitInvoker.RunAsync(rootPath, ["update-ref", "-d", snapshotRef], ct, killToken: CancellationToken.None);
                }

                // Write sidecar.
                step = "sidecar";
                var sidecarPath = Path.Combine(taskDirectory, SidecarFileName);
                var sidecar = JsonSerializer.Serialize(
                    new FlaggedWorkSidecar(runBaseSha, createdUtc, flaggedStage),
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                await File.WriteAllTextAsync(sidecarPath, sidecar, ct);
                return CaptureResult.Captured;
            }
            finally
            {
                try { File.Delete(tempIndex.Io); } catch { /* best-effort */ }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a snapshot failure block the flag, and never let it pass unsaid.
            return CaptureResult.Failed(step, ex.Message);
        }
    }

    // The bundle as the git serving this workspace is handed it: relative to the
    // root, forward slashes. Every invocation is `git -C <root>`, so both gits
    // resolve it against that root — and the git of a workspace inside a WSL distro
    // runs there, where the absolute path VR opens is a Windows path naming nothing.
    private static string RepoRelativeBundle(string rootPath, string taskDirectory) =>
        Path.GetRelativePath(rootPath, Path.Combine(taskDirectory, BundleFileName)).Replace('\\', '/');

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

    /// <summary>
    /// What a capture did: saved the work into the bundle, found no run base to snapshot
    /// against (not a failure), or failed at a named step with what git said.
    /// </summary>
    internal sealed record CaptureResult
    {
        /// <summary>How much of git's own explanation a failed capture carries.</summary>
        private const int OutputTailChars = 300;

        public bool IsCaptured { get; private init; }

        /// <summary>
        /// The step that failed, or null: the git command it ran (read-tree, add, restore, rm,
        /// write-tree, commit-tree, update-ref, bundle), or the file work around them (prepare, sidecar).
        /// </summary>
        public string? FailedStep { get; private init; }

        /// <summary>The tail of what the failed step said, on one line; null when nothing failed.</summary>
        public string? Output { get; private init; }

        public bool IsFailed => FailedStep is not null;

        public static CaptureResult Captured { get; } = new() { IsCaptured = true };
        public static CaptureResult NothingToCapture { get; } = new();

        public static CaptureResult Failed(string step, string output)
        {
            var text = output.Trim().ReplaceLineEndings(" ");
            return new CaptureResult
            {
                FailedStep = step,
                Output = text.Length <= OutputTailChars ? text : "…" + text[^OutputTailChars..]
            };
        }
    }
}
