using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Shared test helpers for Swival* test files. Consolidates the <c>AlwaysReady</c>,
/// <c>Invocation</c>, and <c>WriteExecutableAsync</c> helpers that were duplicated
/// verbatim across six test files.
/// </summary>
internal static class SwivalTestHelpers
{
    public static Task<BackendReadiness> AlwaysReady(CancellationToken _) =>
        Task.FromResult(new BackendReadiness(true, null));

    public static StageInvocation Invocation(string rootPath) =>
        new(
            RelayStages.All[0],
            "cheap",
            "run-1",
            rootPath,
            "task",
            "# Task",
            string.Empty,
            [],
            [],
            Path.Combine(rootPath, ".relay", "task", "stage1-attempt1"),
            Path.Combine(rootPath, ".relay", "task", "stage1-attempt1.report.json"),
            1);

    public static async Task<string> WriteExecutableAsync(string rootPath, string name, string text)
    {
        var path = Path.Combine(rootPath, name);
        await File.WriteAllTextAsync(path, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
