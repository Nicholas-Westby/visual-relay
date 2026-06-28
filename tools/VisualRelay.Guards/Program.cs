using VisualRelay.Guards;

// VisualRelay.Guards — the C# home for the repo's policy guards (ports of the
// retired tools/guards/*.sh). Dispatch on the first arg:
//   shell-size (default) — enforcing: shell scripts over the logic-line limit (exit 1)
//   file-size            — *.cs/*.axaml under src/tests/tools over the limit
//   source-enumeration   — stale virtio-fs/readdir cache detector (pre-build)
//   sync-over-async      — .Result/.GetAwaiter().GetResult()/.Wait() in test methods
// shell-size is the default so `./visual-relay guards` keeps working.

var sub = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "shell-size";
var rest = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[1..] : args;

var repoRoot = GuardRepoRoot.Resolve();
if (repoRoot is null)
{
    Console.Error.WriteLine("guards: could not find repo root (no visual-relay file walking up).");
    return 1; // every guard is blocking — a missing root is a failure, not a pass
}

return sub switch
{
    "shell-size" => await ShellSizeGuardRunner.RunAsync(repoRoot, rest),
    "file-size" => FileSizeGuardRunner.Run(repoRoot),
    "source-enumeration" => await SourceEnumerationGuardRunner.RunAsync(repoRoot),
    "sync-over-async" => SyncOverAsyncGuardRunner.Run(repoRoot),
    _ => Unknown(sub),
};

static int Unknown(string sub)
{
    Console.Error.WriteLine($"guards: unknown subcommand '{sub}' (expected shell-size|file-size|source-enumeration|sync-over-async).");
    return 2;
}
