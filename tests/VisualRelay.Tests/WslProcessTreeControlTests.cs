using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The WSL strategy behind <see cref="VisualRelay.Core.Execution.IProcessTreeControl"/>:
/// it learns the sandbox root's pid from the file the envelope wrote, samples CPU
/// from one read of the distro's /proc stat lines, and signals the root's process group,
/// every step a plain <c>wsl.exe --exec</c> launch handed to an injected runner.
/// </summary>
public sealed class WslProcessTreeControlTests
{
    private const string PidFile = "/tmp/visual-relay/run-1-attempt-1.pid";
    private const string Sh = "/bin/sh";

    // Stat lines (utime and stime are fields 14 and 15) at 100 ticks a second: the root's tree
    // is 4242 (1 s) and its child 4250 (90 s); 1 and 5000 are not in it.
    private const string ProcStat =
        "1 (init) S 0 1 1 0 -1 4194560 100 0 0 0 150 50 0 0 20 0 1 0 1 0 0\n" +
        "4242 (nono) S 311 4242 311 0 -1 4194560 100 0 0 0 60 40 0 0 20 0 1 0 1 0 0\n" +
        "4250 (java (main)) S 4242 4242 311 0 -1 4194560 100 0 0 0 8000 1000 0 0 20 0 30 0 1 0 0\n" +
        "5000 (cron) S 1 5000 5000 0 -1 4194560 100 0 0 0 50000 10000 0 0 20 0 1 0 1 0 0\n" +
        "hz 100\n";

    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    private static WslProcessTreeControl Control(ScriptedRunner runner) => new(Context, PidFile, runner.RunAsync);

    [Fact]
    public async Task Sample_ReadsThePidFileOnce_ThenSumsTheRootsTreeFromProcStat()
    {
        var runner = new ScriptedRunner();
        var control = Control(runner);

        var first = await control.SampleCpuMsAsync(CancellationToken.None);
        var second = await control.SampleCpuMsAsync(CancellationToken.None);

        Assert.Equal(91_000, first);
        Assert.Equal(91_000, second);
        Assert.Equal(["cat", Sh, Sh], runner.Programs);
    }

    [Fact]
    public async Task Sample_PidFileNotWrittenYet_IsNoSignalAndIsRetriedNextTime()
    {
        var runner = new ScriptedRunner();
        runner.PidReplies.Enqueue((1, "cat: /tmp/visual-relay/run-1-attempt-1.pid: No such file or directory"));
        var control = Control(runner);

        var early = await control.SampleCpuMsAsync(CancellationToken.None);
        var later = await control.SampleCpuMsAsync(CancellationToken.None);

        Assert.Null(early);
        Assert.Equal(91_000, later);
        Assert.Equal(["cat", "cat", Sh], runner.Programs);
    }

    [Fact]
    public async Task Sample_PidFileGarbage_IsNoSignal()
    {
        var runner = new ScriptedRunner();
        runner.PidReplies.Enqueue((0, "not a pid\n"));
        var control = Control(runner);

        Assert.Null(await control.SampleCpuMsAsync(CancellationToken.None));
        Assert.Equal(["cat"], runner.Programs);
    }

    [Fact]
    public async Task Sample_ReadFailing_IsNoSignal()
    {
        var runner = new ScriptedRunner { SampleReply = (126, "/bin/sh: cannot execute") };
        var control = Control(runner);

        Assert.Null(await control.SampleCpuMsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stop_GracefulSendsTermToTheGroup_ForcedSendsKill()
    {
        var runner = new ScriptedRunner();
        var control = Control(runner);

        await control.StopAsync(graceful: true, CancellationToken.None);
        await control.StopAsync(graceful: false, CancellationToken.None);

        // The forced stop is the reap step, so it also takes the pid file with it.
        Assert.Equal(["cat", "kill", "kill", "rm"], runner.Programs);
        Assert.Equal(["-d", "Ubuntu", "--exec", "kill", "-TERM", "--", "-4242"], runner.Launches[1].Arguments);
        Assert.Equal(["-d", "Ubuntu", "--exec", "kill", "-KILL", "--", "-4242"], runner.Launches[2].Arguments);
    }

    /// <summary>
    /// The pid file the envelope wrote is removed once the tree is forced down —
    /// the reap step every run ends with. A graceful stop is not the end of the
    /// sequence, so it leaves the file for the forced stop that may follow.
    /// </summary>
    [Fact]
    public async Task Stop_Forced_RemovesThePidFile_AndGracefulDoesNot()
    {
        var runner = new ScriptedRunner();
        var control = Control(runner);

        await control.StopAsync(graceful: true, CancellationToken.None);
        Assert.DoesNotContain("rm", runner.Programs);

        await control.StopAsync(graceful: false, CancellationToken.None);

        Assert.Equal(["-d", "Ubuntu", "--exec", "rm", "-f", "--", PidFile], runner.Launches[^1].Arguments);
    }

    [Fact]
    public async Task Stop_WithoutAPid_SendsNoSignal()
    {
        var runner = new ScriptedRunner();
        runner.PidReplies.Enqueue((1, ""));
        var control = Control(runner);

        await control.StopAsync(graceful: true, CancellationToken.None);

        Assert.Equal(["cat"], runner.Programs);
    }

    [Fact]
    public async Task EveryLaunch_UsesTheContextsExeAndDistroAndTheWslEnvironment()
    {
        var runner = new ScriptedRunner();
        var control = Control(runner);

        await control.SampleCpuMsAsync(CancellationToken.None);

        Assert.All(runner.Launches, launch =>
        {
            Assert.Equal(Context.WslExePath, launch.FileName);
            Assert.Equal(["-d", "Ubuntu", "--exec"], launch.Arguments.Take(3));
            Assert.Equal("1", launch.Environment["WSL_UTF8"]);
        });
        Assert.Equal(["cat", PidFile], runner.Launches[0].Arguments.Skip(3));
        Assert.Equal([Sh, "-c", ProcessTreeCpuSampler.ProcStatScript], runner.Launches[1].Arguments.Skip(3));
    }

    private sealed class ScriptedRunner
    {
        public List<WslLaunch> Launches { get; } = [];
        public Queue<(int ExitCode, string Output)> PidReplies { get; } = new();
        public (int ExitCode, string Output) SampleReply { get; init; } = (0, ProcStat);

        public IEnumerable<string> Programs => Launches.Select(l => l.Arguments[3]);

        public Task<(int ExitCode, string Output)> RunAsync(WslLaunch launch, CancellationToken ct)
        {
            Launches.Add(launch);
            return Task.FromResult(launch.Arguments[3] switch
            {
                "cat" => PidReplies.Count > 0 ? PidReplies.Dequeue() : (0, "4242\n"),
                Sh => SampleReply,
                "kill" or "rm" => (0, ""),
                _ => (127, "unexpected program"),
            });
        }
    }
}
