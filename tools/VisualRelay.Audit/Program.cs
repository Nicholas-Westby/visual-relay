using System.Diagnostics;
using VisualRelay.Audit;
using VisualRelay.Guards;

// VisualRelay.Audit — the diagnostic CLI for hermeticity findings.
// ./visual-relay audit [rule-id ...]
// No args = all four rules. Exit 0 when audit ran; exit 2 on unknown rule-id.
// Informational only — never a gate; see tasks 02-04 for the enforcing guard-as-tests.

var root = GuardRepoRoot.Resolve();
if (root is null)
{
    Console.Error.WriteLine("audit: could not find repo root.");
    return 1;
}

// Parse rule-id filter.
var allRuleIds = new[] { "retry-delay-loops", "di-bypass", "real-waits", "test-side-effects" };
var requestedRules = args.Length > 0
    ? args.Select(a => a.Trim().ToLowerInvariant()).ToArray()
    : allRuleIds;

foreach (var rule in requestedRules)
{
    if (!allRuleIds.Contains(rule))
    {
        Console.Error.WriteLine($"audit: unknown rule-id '{rule}' (expected: {string.Join("|", allRuleIds)}).");
        return 2;
    }
}

// Scan src/, tests/, tools/ for *.cs files.
var files = ScanSources(root);

var stopwatch = Stopwatch.StartNew();

var findings = new List<AuditRenderer.Finding>();

// Map rule-ids to matcher calls.
var registry = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase)
{
    ["retry-delay-loops"] = () =>
    {
        var violations = RetryDelayLoopsGuard.FindViolations(files);
        foreach (var v in violations)
            findings.Add(new AuditRenderer.Finding("retry-delay-loops", v.Path, v.Line, v.Snippet, v.Reason,
                "This loop contains both a delay call (Task.Delay/Thread.Sleep) AND a git/process invocation.",
                "Consider: extract the delay from the retry loop, or make the operation idempotent so retry is unnecessary. Check if the failure being retried can ever succeed (e.g. exit-128 is deterministic)."));
    },
    ["di-bypass"] = () =>
    {
        var violations = DiBypassGuard.FindViolations(files);
        foreach (var v in violations)
            findings.Add(new AuditRenderer.Finding("di-bypass", v.Path, v.Line, v.Snippet, v.Reason,
                "A call site omits an optional parameter that defaults to a real collaborator, while the enclosing class holds RelayDriverDependencies with the injected seam.",
                "Pass the injected dependency explicitly, e.g. `_dependencies.GitInvoker` or `_dependencies.TimeProvider`."));
    },
    ["real-waits"] = () =>
    {
        var violations = RealWaitsGuard.FindViolations(files);
        foreach (var v in violations)
            findings.Add(new AuditRenderer.Finding("real-waits", v.Path, v.Line, v.Snippet, v.Reason,
                "A real wall-clock wait (Thread.Sleep, Task.Delay without TimeProvider, or shell sleep) was found.",
                "Drive the delay with a TimeProvider (virtual-clock seam) or use Task.Yield() for advance-yield loops. For shell sleeps, prefer an in-process equivalent or a virtual-clock-driven timer."));

        var suppressions = RealWaitsGuard.FindSuppressions(files);
        foreach (var s in suppressions)
            findings.Add(new AuditRenderer.Finding("real-waits:suppression", s.Path, s.Line, s.Reason,
                "A // vr-allow-sleep: suppression marker — the reason should be re-reviewed.",
                "Confirm the suppression reason is still valid. If the underlying need is gone, remove the suppression.",
                "Revisit this suppression and either remove it or update the reason."));
    },
    ["test-side-effects"] = () =>
    {
        var violations = TestSideEffectsGuard.FindViolations(files);
        foreach (var v in violations)
            findings.Add(new AuditRenderer.Finding("test-side-effects", v.Path, v.Line, v.Snippet, v.Reason,
                "A test source references a real-side-effect construct.",
                "Replace with a hermetic double: NullGitInvoker/GitSim for GitInvoker, ScriptedTestRunner for Process.Start, DictionaryEnvironmentAccessor for Environment.SetEnvironmentVariable."));
    },
};

foreach (var rule in requestedRules)
{
    registry[rule]();
}

stopwatch.Stop();

AuditRenderer.Render(findings, stopwatch.Elapsed);

return 0;

// ── Helpers ──

static List<(string Path, string Source)> ScanSources(string root)
{
    var results = new List<(string Path, string Source)>();

    foreach (var dir in new[] { "src", "tests", "tools" })
    {
        var fullDir = Path.Combine(root, dir);
        if (!Directory.Exists(fullDir))
            continue;

        foreach (var file in Directory.EnumerateFiles(fullDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file))
                continue;

            var relativePath = Path.GetRelativePath(root, file);
            var source = File.ReadAllText(file);
            results.Add((relativePath, source));
        }
    }

    return results;
}

static bool IsBuildArtifact(string path)
{
    var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return segments.Any(s => s is "bin" or "obj");
}
