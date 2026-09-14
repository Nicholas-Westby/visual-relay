namespace VisualRelay.Core.Execution;

internal static partial class FlaggedWorkStore
{
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

        // .NET opens the absolute path; git is handed the repo-relative one.
        var bundleArg = RepoRelativeBundle(rootPath, taskDirectory);

        // Verify the bundle.
        var verifyResult = await gitInvoker.RunAsync(rootPath, ["bundle", "verify", bundleArg], ct);
        if (verifyResult.ExitCode != 0)
            return RestoreResult.Unrestorable;

        // Fetch the snapshot commit from the bundle. The bundle records the snapshot
        // under refs/relay-snapshot/<taskId>.
        var snapshotRef = $"refs/relay-snapshot/{taskId}";
        var fetchRef = $"refs/relay-resume/{taskId}";
        var fetchResult = await gitInvoker.RunAsync(
            rootPath, ["fetch", bundleArg, $"+{snapshotRef}:{fetchRef}"], ct);
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
