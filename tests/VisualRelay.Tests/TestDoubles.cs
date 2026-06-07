using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Logging;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

internal sealed class TestRepository : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "visual-relay-tests", Guid.NewGuid().ToString("N"));

    private TestRepository()
    {
        Directory.CreateDirectory(Root);
    }

    public static TestRepository Create() => new();

    public string AttemptReportPath(string taskId, int stage, int attempt) =>
        Path.Combine(Root, ".relay", taskId, $"stage{stage}-attempt{attempt}.report.json");

    public void WriteConfig(string testCommand, string[] logSources)
    {
        Directory.CreateDirectory(Path.Combine(Root, ".relay"));
        File.WriteAllText(
            Path.Combine(Root, ".relay", "config.json"),
            $$"""
            {
              "testCmd": "{{testCommand}}",
              "logSources": [{{string.Join(",", logSources.Select(s => $"\"{s}\""))}}]
            }
            """);
    }

    public void WriteTask(string id, string markdown)
    {
        var path = Path.Combine(Root, "llm-tasks", $"{id}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, markdown);
    }

    public void WriteNestedTask(string id, string markdown, params (string Name, string Content)[] siblings)
    {
        var dir = Path.Combine(Root, "llm-tasks", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{id}.md"), markdown);
        foreach (var sibling in siblings)
        {
            File.WriteAllText(Path.Combine(dir, sibling.Name), sibling.Content);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class InMemoryRelayEventSink : IRelayEventSink
{
    public List<RelayEvent> Events { get; } = [];

    public Task PublishAsync(RelayEvent relayEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(relayEvent);
        return Task.CompletedTask;
    }
}

internal sealed class ScriptedSubagentRunner : ISubagentRunner
{
    private string _codeFile = "src/app.cs";
    private string _testFile = "tests/app.tests.cs";
    private bool _nonCodeOnly;
    private string _nonCodeFile = "docs/README.md";
    private bool _codeOnly;
    private string _codeOnlyFile = "src/View.axaml";
    private bool _testOnly;
    private string _testOnlyFile = "tests/regression.cs";

    public void SeedHappyPath(string codeFile, string testFile)
    {
        _codeFile = codeFile;
        _testFile = testFile;
    }

    // A non-code change: the manifest contains only documentation/config files
    // (e.g. .md, .txt, .json). Stage 5 returns no testFiles.
    public void SeedNonCodeOnly(string nonCodeFile)
    {
        _nonCodeOnly = true;
        _nonCodeFile = nonCodeFile;
    }

    // A code-only change: the manifest contains only implementation code files
    // (e.g. .axaml, .ts, .py) with no authored tests. Stage 5 returns no testFiles.
    public void SeedCodeOnly(string codeFile)
    {
        _codeOnly = true;
        _codeOnlyFile = codeFile;
    }

    // A test-only change: the manifest contains only test files (already covered
    // by existing tests). Stage 5 returns the test file as a testFile.
    public void SeedTestOnly(string testFile)
    {
        _testOnly = true;
        _testOnlyFile = testFile;
    }

    public Task<SubagentResult> RunAsync(StageInvocation invocation, CancellationToken cancellationToken = default)
    {
        var json = invocation.Stage.Number switch
        {
            1 => """{"summary":"framed","options":["small"]}""",
            2 => """{"findings":"found","constraints":[]}""",
            3 => """{"evidence":"no remnants","excerpts":[],"repro":"none"}""",
            4 when _nonCodeOnly => $$"""{"plan":"edit docs","manifest":["{{_nonCodeFile}}"]}""",
            4 when _codeOnly => $$"""{"plan":"edit code","manifest":["{{_codeOnlyFile}}"]}""",
            4 when _testOnly => $$"""{"plan":"add tests","manifest":["{{_testOnlyFile}}"]}""",
            4 => $$"""{"plan":"edit files","manifest":["{{_codeFile}}","{{_testFile}}"]}""",
            5 when _nonCodeOnly => """{"testFiles":[],"rationale":"documentation-only; nothing to unit-test"}""",
            5 when _codeOnly => """{"testFiles":[],"rationale":"code change without authored tests"}""",
            5 when _testOnly => $$"""{"testFiles":["{{_testOnlyFile}}"],"rationale":"test-only change"}""",
            5 => $$"""{"testFiles":["{{_testFile}}"],"rationale":"red first"}""",
            6 => """{"summary":"implemented"}""",
            7 => """{"verdict":"pass","issues":[]}""",
            8 => """{"summary":"fixed review notes"}""",
            9 => """{"summary":"verified","commitMessages":["feat: implement feature","fix: address edge case","chore: update project files"]}""",
            10 => """{"summary":"fixed verify"}""",
            _ => """{"summary":"ok"}"""
        };

        return Task.FromResult(new SubagentResult(
            RawText: $"```json{Environment.NewLine}{json}{Environment.NewLine}```",
            Json: json,
            IsValid: true,
            Error: null));
    }
}

internal sealed class ScriptedTestRunner : ITestRunner
{
    private readonly Queue<TestRunResult> _results;

    public ScriptedTestRunner(params TestRunResult[] results)
    {
        _results = new Queue<TestRunResult>(results);
    }

    public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new TestRunResult(0, "green"));
    }
}

/// <summary>
/// Returns a synthetic timeout result — simulating what ShellTestRunner produces
/// when the test command exceeds its cap and the process tree is killed.
/// </summary>
internal sealed class TimeoutSimulatingTestRunner : ITestRunner
{
    public const string Output =
        "test command timed out after 300000ms\n" +
        "The configured time limit was exceeded and the process was halted.\n";

    public Task<TestRunResult> RunAsync(string rootPath, string command, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new TestRunResult(-1, Output, TimedOut: true));
    }
}

internal sealed class RecordingTaskRunner : IRelayTaskRunner
{
    public List<string> TasksRun { get; } = [];
    public Action? AfterRun { get; set; }
    public Func<Task>? AfterRunAsync { get; set; }

    public async Task<RelayTaskOutcome> RunTaskAsync(string rootPath, string taskId, CancellationToken cancellationToken = default)
    {
        TasksRun.Add(taskId);
        AfterRun?.Invoke();
        if (AfterRunAsync is not null)
        {
            await AfterRunAsync();
        }

        return new RelayTaskOutcome(taskId, RelayTaskOutcomeStatus.Committed, "hash", "commit", null);
    }
}

internal static class TestGit
{
    public static string Run(string rootPath, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(rootPath);
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }
}

internal static class RepoSetup
{
    /// <summary>
    /// The repository root (where the visual-relay script lives), resolved by
    /// walking up from the test assembly directory.
    /// </summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "visual-relay")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName
                ?? throw new InvalidOperationException("Could not find repo root from " + AppContext.BaseDirectory);
        }
    }

    /// <summary>
    /// Copies the project's .githooks/pre-commit into the given repo's .git/hooks/
    /// and makes it executable. The hook file must exist; otherwise this throws.
    /// </summary>
    public static void InstallPreCommitHook(string repoRoot)
    {
        var srcPath = Path.Combine(Root, ".githooks", "pre-commit");
        var hooksDir = Path.Combine(repoRoot, ".git", "hooks");
        Directory.CreateDirectory(hooksDir);
        var destPath = Path.Combine(hooksDir, "pre-commit");
        File.Copy(srcPath, destPath, overwrite: true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
