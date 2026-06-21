using VisualRelay.Core.Execution;
using VisualRelay.Guards;

// ── Resolve limit ──────────────────────────────────────────────────
var limit = 20; // default
var maxArgIndex = Array.IndexOf(args, "--max");
if (maxArgIndex >= 0 && maxArgIndex + 1 < args.Length
    && int.TryParse(args[maxArgIndex + 1], out var parsed))
{
    limit = parsed;
}

var envLimit = Environment.GetEnvironmentVariable("VISUAL_RELAY_SHELL_LINE_LIMIT");
if (envLimit is not null && int.TryParse(envLimit, out var envParsed))
{
    limit = envParsed;
}

// ── Resolve repo root (walk up to find visual-relay) ──────────────
var cwd = Environment.CurrentDirectory;
var repoRoot = cwd;
while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot, "visual-relay")))
{
    repoRoot = Path.GetDirectoryName(repoRoot);
}

if (repoRoot is null)
{
    Console.Error.WriteLine("shell-size: could not find repo root (no visual-relay file found walking up from " + cwd + ")");
    return 0; // advisory — exit 0
}

// ── Get tracked files via GitInvoker ───────────────────────────────
var git = new GitInvoker();
var (exitCode, output, timedOut) = await git.RunAsync(
    repoRoot,
    ["ls-files"],
    CancellationToken.None);

if (exitCode != 0 || timedOut)
{
    Console.Error.WriteLine("shell-size: git ls-files failed (exit " + exitCode + ")");
    return 0; // advisory — exit 0
}

var trackedFiles = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Select(f => f.TrimEnd('\r'))
    .ToArray();

// ── Read files ────────────────────────────────────────────────────
var fileData = new List<(string Path, string[] Lines)>();
foreach (var file in trackedFiles)
{
    var fullPath = Path.Combine(repoRoot, file);
    if (!File.Exists(fullPath))
        continue;

    string[] allLines;
    try
    {
        allLines = File.ReadAllLines(fullPath);
    }
    catch
    {
        continue; // skip unreadable files (e.g. permissions)
    }

    fileData.Add((file, allLines));
}

// ── Find violations ───────────────────────────────────────────────
var violations = ShellSizeGuard.FindViolations(fileData, limit);

// ── Print ─────────────────────────────────────────────────────────
if (violations.Count > 0)
{
    foreach (var v in violations)
    {
        Console.WriteLine($"{v.Path}: {v.Count} logic lines (limit {v.Limit})");
        Console.WriteLine("  → move the logic into a C# tool and leave a thin wrapper; there is no allowlist.");
    }
}

Console.WriteLine($"shell-size: {violations.Count} script(s) over the limit.");
return 0;
