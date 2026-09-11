using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The WSL strategy behind <see cref="VisualRelay.Core.Execution.IProcessTreeControl"/>:
/// it learns the sandbox root's pid from the file the envelope wrote, samples CPU
/// with one <c>ps</c> inside the distro, and signals the root's process group,
/// every step a plain <c>wsl.exe --exec</c> launch handed to an injected runner.
/// </summary>
public sealed class WslProcessTreeControlTests
{
    private const string PidFile = "/tmp/visual-relay/run-1-attempt-1.pid";
    private const string Ps =
        "      1       0 00:00:02\n" +
        "   4242     311 00:00:01\n" +
        "   4250    4242 00:01:30\n" +
        "   5000       1 00:10:00\n";

    private static readonly WslContext Context =
        new(@"C:\Windows\System32\wsl.exe", "Ubuntu", "/usr/local/bin/nono", "/home/alice");

    private static WslProcessTreeControl Control(ScriptedRunner runner) => new(Context, PidFile, runner.RunAsync);

    [Fact]
    public async Task Sample_ReadsThePidFileOnce_ThenSumsTheRootsTreeFromPs()
    {
        var runner = new ScriptedRunner();
        var control = Control(runner);

        var first = await control.SampleCpuMsAsync(CancellationToken.None);
        var second = await control.SampleCpuMsAsync(CancellationToken.None);

        Assert.Equal(91_000, first);
        Assert.Equal(91_000, second);
        Assert.Equal(["cat", "ps", "ps"], runner.Programs);
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
        Assert.Equal(["cat", "cat", "ps"], runner.Programs);
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
    public async Task Sample_PsFailing_IsNoSignal()
    {
        var runner = new ScriptedRunner { PsReply = (1, "ps: command not found") };
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

        Assert.Equal(["cat", "kill", "kill"], runner.Programs);
        Assert.Equal(["-d", "Ubuntu", "--exec", "kill", "-TERM", "--", "-4242"], runner.Launches[1].Arguments);
        Assert.Equal(["-d", "Ubuntu", "--exec", "kill", "-KILL", "--", "-4242"], runner.Launches[2].Arguments);
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
        Assert.Equal(["ps", "-axo", "pid=,ppid=,time="], runner.Launches[1].Arguments.Skip(3));
    }

    private sealed class ScriptedRunner
    {
        public List<WslLaunch> Launches { get; } = [];
        public Queue<(int ExitCode, string Output)> PidReplies { get; } = new();
        public (int ExitCode, string Output) PsReply { get; init; } = (0, Ps);

        public IEnumerable<string> Programs => Launches.Select(l => l.Arguments[3]);

        public Task<(int ExitCode, string Output)> RunAsync(WslLaunch launch, CancellationToken ct)
        {
            Launches.Add(launch);
            return Task.FromResult(launch.Arguments[3] switch
            {
                "cat" => PidReplies.Count > 0 ? PidReplies.Dequeue() : (0, "4242\n"),
                "ps" => PsReply,
                "kill" => (0, ""),
                _ => (127, "unexpected program"),
            });
        }
    }
}
