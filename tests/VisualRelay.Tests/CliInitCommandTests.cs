namespace VisualRelay.Tests;

/// <summary>
/// Behavioral tests for VisualRelay.Cli's <c>init</c> command (re-pointed from the
/// bash launcher's <c>init</c> dispatch + CwdSandbox tests). init must: prefer a
/// published self-contained <c>init/VisualRelay.Init</c> binary; otherwise run the
/// Init tool with an absolute <c>--project</c> path; forward ORIGINAL_CWD when no
/// path is given; and pass an explicit path through when supplied.
/// </summary>
public sealed class CliInitCommandTests
{
    [Fact]
    public async Task Init_NoArgs_RunsInitToolWithOriginalCwd_AbsoluteProject()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo();
        var argv = Path.Combine(repo, "dotnet-argv");
        try
        {
            // Stub dotnet records its full argv (one per line).
            CliHarness.WriteStub(stub, "dotnet", $"printf '%s\\n' \"$@\" >> '{argv}'\nexit 0");
            await CliHarness.RunAsync(repo, stub, ["init"]); // ORIGINAL_CWD defaults to repo in the harness

            var lines = File.ReadAllLines(argv);
            var projIdx = Array.IndexOf(lines, "--project");
            Assert.True(projIdx >= 0 && projIdx + 1 < lines.Length, "init must pass --project");
            Assert.StartsWith("/", lines[projIdx + 1], StringComparison.Ordinal); // absolute

            var dashIdx = Array.IndexOf(lines, "--");
            Assert.True(dashIdx >= 0 && dashIdx + 1 < lines.Length, "init must forward a root after --");
            Assert.Equal(repo, lines[dashIdx + 1]);
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task Init_ExplicitPath_TakesPrecedenceOverOriginalCwd()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo();
        var argv = Path.Combine(repo, "dotnet-argv");
        const string explicitPath = "/explicit/test/path/for/vr";
        try
        {
            CliHarness.WriteStub(stub, "dotnet", $"printf '%s\\n' \"$@\" >> '{argv}'\nexit 0");
            await CliHarness.RunAsync(repo, stub, ["init", explicitPath]);

            var lines = File.ReadAllLines(argv);
            var dashIdx = Array.IndexOf(lines, "--");
            Assert.True(dashIdx >= 0 && dashIdx + 1 < lines.Length);
            Assert.Equal(explicitPath, lines[dashIdx + 1]);
        }
        finally { TryDelete(repo); }
    }

    [Fact]
    public async Task Init_PrefersPublishedBinary_OverDotnetRun()
    {
        var (repo, stub) = CliHarness.NewSandboxRepo();
        var publishedRan = Path.Combine(repo, "published-ran");
        var dotnetRan = Path.Combine(repo, "dotnet-ran");
        try
        {
            // Published init binary at init/VisualRelay.Init.
            var initDir = Path.Combine(repo, "init");
            CliHarness.WriteStub(initDir, "VisualRelay.Init", $"echo ran > '{publishedRan}'\nexit 0");
            CliHarness.WriteStub(stub, "dotnet", $"echo ran > '{dotnetRan}'\nexit 0");

            await CliHarness.RunAsync(repo, stub, ["init"]);

            Assert.True(File.Exists(publishedRan), "the published init binary must be preferred");
            Assert.False(File.Exists(dotnetRan), "dotnet run must not be used when the published binary exists");
        }
        finally { TryDelete(repo); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* best-effort */ }
    }
}
