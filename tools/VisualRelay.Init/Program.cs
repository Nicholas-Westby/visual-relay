using VisualRelay.Core.Execution;
using VisualRelay.Core.Init;
using VisualRelay.Domain;

var rootPath = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
if (!Directory.Exists(rootPath))
{
    Console.Error.WriteLine($"visual-relay init: directory not found: {rootPath}");
    return 2;
}

var candidates = TestCommandDetector.DetectCandidates(rootPath);
string? validatedCommand = null;
ValidationResult? lastResult = null;

if (candidates.Count > 0)
{
    // Smoke-run each candidate with a 5-second timeout. The first one that
    // can actually start is persisted; the rest are discarded.
    var runner = new DirectExecTestRunner(TimeSpan.FromSeconds(5));
    var validator = new TestCommandValidator(runner);

    foreach (var candidate in candidates)
    {
        var result = await validator.ValidateAsync(rootPath, candidate);
        lastResult = result;

        if (result.Accepted)
        {
            validatedCommand = candidate;
            break;
        }
    }
}

string path;
string? summarySuffix;

if (validatedCommand is not null)
{
    path = RelayConfigWriter.Write(rootPath, validatedCommand);
    var summary = FormatSummary(lastResult!.RunResult);
    summarySuffix = $"testCmd validated: {validatedCommand}{summary}";
}
else
{
    path = RelayConfigWriter.Write(rootPath, null);
    summarySuffix = "could not validate a test command — wrote testCmd: null";
}

var hookResult = await HookInstaller.InstallAsync(rootPath, CancellationToken.None);
if (hookResult is { Installed: false, Warning: not null })
{
    Console.Error.WriteLine(hookResult.Warning);
}

Console.WriteLine($"Wrote {path} with {summarySuffix}.");

return 0;

static string FormatSummary(TestRunResult r)
{
    if (r.ExitCode == 0)
    {
        // Extract a short summary: first relevant line or a count.
        var firstLine = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        if (firstLine.Length > 80)
            firstLine = string.Concat(firstLine.AsSpan(0, 80), "…");
        return string.IsNullOrEmpty(firstLine) ? " (ok)" : $" ({firstLine})";
    }

    // Non-zero but accepted — tests failed, runner is real.
    return $" (exit {r.ExitCode})";
}
