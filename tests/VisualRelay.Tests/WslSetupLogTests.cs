using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The log of one <c>setup-wsl</c> run. On 2026-09-19 the only evidence of why setup failed was a
/// line WSL printed for a step that exited 0 (it had to restart Windows first), and setup had
/// thrown it away, so every call is now kept in full.
/// </summary>
public sealed class WslSetupLogTests
{
    private static readonly string LongOutput =
        string.Join('\n', Enumerable.Range(1, 200).Select(i => $"Get:{i} http://archive.ubuntu.com")) + "\n";

    [Fact]
    public async Task EveryCall_IsRecordedInFull_WithItsExitCode()
    {
        using var dir = new TempDirectory();
        var log = new WslSetupLog(Path.Combine(dir.Path, "logs", "setup-wsl.log"));
        var run = log.Recording(Answering(0, LongOutput), "wsl.exe");

        await run(["-d", "Ubuntu", "-u", "root", "--exec", "sh", "-c", "apt-get update", "vr-setup"], CancellationToken.None);

        var text = await File.ReadAllTextAsync(log.Path);
        Assert.Contains("wsl.exe -d Ubuntu -u root --exec sh -c \"apt-get update\" vr-setup", text);
        Assert.Contains("exit 0", text);
        Assert.Contains("Get:1 http://archive.ubuntu.com", text);
        Assert.Contains("Get:200 http://archive.ubuntu.com", text);
    }

    [Fact]
    public async Task TheRecordedRunner_HandsBackWhatTheRealOneReturned()
    {
        using var dir = new TempDirectory();
        var run = new WslSetupLog(Path.Combine(dir.Path, "setup-wsl.log")).Recording(Answering(100, "E: no\n"), "wsl.exe");

        var result = await run(["--install", "Ubuntu", "--no-launch"], CancellationToken.None);

        Assert.Equal((100, "E: no\n"), result);
    }

    [Fact]
    public async Task ANewRun_ReplacesThePreviousRunsLog()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "setup-wsl.log");
        await new WslSetupLog(path).Recording(Answering(0, "first run\n"), "wsl.exe")(["-l", "-v"], CancellationToken.None);

        var run = new WslSetupLog(path).Recording(Answering(0, "second run\n"), "wsl.exe");
        await run(["-l", "-v"], CancellationToken.None);
        await run(["--version"], CancellationToken.None);

        var text = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("first run", text);
        Assert.Contains("wsl.exe -l -v", text);
        Assert.Contains("wsl.exe --version", text);
    }

    [Fact]
    public async Task ALogThatCannotBeWritten_NeverStopsSetup()
    {
        using var dir = new TempDirectory();
        var aFile = Path.Combine(dir.Path, "a-file");
        await File.WriteAllTextAsync(aFile, "");
        var run = new WslSetupLog(Path.Combine(aFile, "setup-wsl.log")).Recording(Answering(0, "ok\n"), "wsl.exe");

        var result = await run(["-l", "-v"], CancellationToken.None);

        Assert.Equal((0, "ok\n"), result);
    }

    /// <summary>The console shows the end of the failed step only; the log has every call in full.</summary>
    [Fact]
    public async Task AFailedSetup_SaysWhereItsLogIs()
    {
        using var dir = new TempDirectory();
        var log = new WslSetupLog(Path.Combine(dir.Path, "setup-wsl.log"));

        var failure = await FailedSetupAsync(log);

        Assert.Contains(log.Path, failure);
    }

    [Fact]
    public async Task AFailedSetup_NeverPointsAtALogThatCouldNotBeWritten()
    {
        using var dir = new TempDirectory();
        var aFile = Path.Combine(dir.Path, "a-file");
        await File.WriteAllTextAsync(aFile, "");
        var log = new WslSetupLog(Path.Combine(aFile, "setup-wsl.log"));

        var failure = await FailedSetupAsync(log);

        Assert.DoesNotContain(log.Path, failure);
    }

    /// <summary>Sets up a distro without nono through a wsl.exe whose every call fails, recorded in <paramref name="log"/>.</summary>
    private static async Task<string> FailedSetupAsync(WslSetupLog log)
    {
        var host = new WslSetupHost(
            _ => throw new InvalidOperationException("the runner probed"),
            log.Recording(Answering(100, "E: no\n"), "wsl.exe"),
            log.Recording(Answering(100, "E: no\n"), "wsl.exe as administrator"),
            "alice",
            LocalNonoDeb: null) { Log = log };
        var outcome = await WslSetupRunner.RunAsync(
            WslSetupPlan.For(WslProbeFixtures.NonoMissing(), "alice"), host, _ => { }, CancellationToken.None);
        return outcome.Failure!;
    }

    private static Func<IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> Answering(
        int exitCode, string output) => (_, _) => Task.FromResult((exitCode, output));
}
