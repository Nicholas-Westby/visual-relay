using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The wsl.exe launch is an argv array, never a string: with <c>--exec</c> wsl.exe
/// splits its command line with CommandLineToArgvW and the distro execs that argv
/// with no Linux shell in between, so every element VR adds reaches the child
/// verbatim. These tests pin the shape element by element, and that the hostile
/// arguments the spec names (space, quotes, backslash, dollar, backtick, newline,
/// non-ASCII) each stay ONE element that VR never re-quotes.
/// </summary>
public sealed class WslLauncherArgvTests
{
    private const string Exe = @"C:\Windows\System32\wsl.exe";
    private const string Workspace = "/home/u/my repo";
    private const string PidFile = "/tmp/visual-relay/run-1-attempt-1.pid";

    private static readonly string[] Prefix =
    [
        "/usr/local/bin/nono", "run", "--profile", "/home/u/.config/visual-relay/vr-guard.json",
        "--allow-cwd", "--silent", "--",
    ];

    public static TheoryData<string> HostileArguments => new()
    {
        "a b", "c\"d", "e'f", "g\\h", "$i", "`j`", "k\nl", "héllo/日本", "",
    };

    [Theory]
    [MemberData(nameof(HostileArguments))]
    public void Build_EveryArgumentStaysOneElementVerbatim(string hostile)
    {
        var launch = WslLauncher.Build(Exe, "Ubuntu", Workspace, PidFile, Prefix, "/bin/sh", ["-c", hostile]);

        Assert.Equal(hostile, launch.Arguments[^1]);
        Assert.Equal("-c", launch.Arguments[^2]);
        Assert.Equal("/bin/sh", launch.Arguments[^3]);
    }

    [Fact]
    public void Build_ProducesTheDocumentedShapeElementByElement()
    {
        var launch = WslLauncher.Build(
            Exe, "Ubuntu", Workspace, PidFile, Prefix, "dotnet", ["test", "--no-build"],
            env: new Dictionary<string, string> { ["CI"] = "1", ["FOO"] = "bar baz" },
            envRemove: ["DEVELOPER_DIR", "SDKROOT"]);

        Assert.Equal(Exe, launch.FileName);
        string[] expected =
        [
            "-d", "Ubuntu", "--exec", "/bin/sh", "-c", WslLauncher.Envelope, "vr", PidFile, Workspace,
            "env", "-u", "DEVELOPER_DIR", "-u", "SDKROOT", "CI=1", "FOO=bar baz",
            "/usr/local/bin/nono", "run", "--profile", "/home/u/.config/visual-relay/vr-guard.json",
            "--allow-cwd", "--silent", "--",
            "dotnet", "test", "--no-build",
        ];
        Assert.Equal(expected, launch.Arguments);
    }

    [Fact]
    public void Envelope_IsTheDocumentedScript()
    {
        // setsid puts the sandbox root in its own session/process group (so one
        // kill -pgid reaches the whole tree); the pid lands in the pid file; cd
        // failing exits 127 loudly because wsl.exe's own --cd is non-fatal.
        Assert.Equal(
            "p=$1; d=$2; shift 2; cd \"$d\" || exit 127; setsid \"$@\" & c=$!; echo \"$c\" > \"$p\"; wait \"$c\"",
            WslLauncher.Envelope);
    }

    [Fact]
    public void Build_ExecPrecedesTheShell_AndTheEnvelopeIsPassedAsOneElement()
    {
        var launch = WslLauncher.Build(Exe, "Ubuntu", Workspace, PidFile, Prefix, "true", []);

        var exec = launch.Arguments.ToList().IndexOf("--exec");
        Assert.Equal(2, exec);
        Assert.Equal("/bin/sh", launch.Arguments[exec + 1]);
        Assert.Equal("-c", launch.Arguments[exec + 2]);
        Assert.Same(WslLauncher.Envelope, launch.Arguments[exec + 3]);
        Assert.Equal("vr", launch.Arguments[exec + 4]);
    }

    [Fact]
    public void Build_NoEnvironmentChanges_StillRunsThroughEnvSoTheShapeIsFixed()
    {
        var launch = WslLauncher.Build(Exe, "Ubuntu", Workspace, PidFile, Prefix, "true", []);

        var env = launch.Arguments.ToList().IndexOf("env");
        Assert.Equal(9, env);
        Assert.Equal(Prefix, launch.Arguments.Skip(env + 1).Take(Prefix.Length));
        Assert.Equal("true", launch.Arguments[^1]);
    }

    [Fact]
    public void Build_EnvRemovalsComeBeforeAssignments()
    {
        var launch = WslLauncher.Build(
            Exe, "Ubuntu", Workspace, PidFile, Prefix, "true", [],
            env: new Dictionary<string, string> { ["A"] = "1" }, envRemove: ["Z"]);

        var arguments = launch.Arguments.ToList();
        Assert.True(arguments.IndexOf("-u") < arguments.IndexOf("A=1"));
        Assert.Equal("Z", arguments[arguments.IndexOf("-u") + 1]);
    }

    [Fact]
    public void Build_HostileWorkspaceAndPidFile_StayVerbatim()
    {
        var launch = WslLauncher.Build(
            Exe, "Ubuntu", "/home/u/it's \"here\" $HOME", "/tmp/visual-relay/a b.pid", Prefix, "true", []);

        Assert.Equal("/tmp/visual-relay/a b.pid", launch.Arguments[7]);
        Assert.Equal("/home/u/it's \"here\" $HOME", launch.Arguments[8]);
    }

    [Fact]
    public void Build_EnvironmentCarriesBothWslVariables()
    {
        var launch = WslLauncher.Build(Exe, "Ubuntu", Workspace, PidFile, Prefix, "true", []);

        Assert.Equal("1", launch.Environment["WSL_UTF8"]);
        Assert.Equal("1", launch.Environment["WSL_DISABLE_WARNINGS"]);
    }

    [Fact]
    public void Build_EnvKeyContainingEquals_IsRejectedRatherThanReshaped()
    {
        Assert.Throws<ArgumentException>(() => WslLauncher.Build(
            Exe, "Ubuntu", Workspace, PidFile, Prefix, "true", [],
            env: new Dictionary<string, string> { ["A=B"] = "1" }));
        Assert.Throws<ArgumentException>(() => WslLauncher.Build(
            Exe, "Ubuntu", Workspace, PidFile, Prefix, "true", [], envRemove: [""]));
    }

    [Fact]
    public void Build_RelativeWorkspaceOrPidFile_IsRejected()
    {
        // The envelope's `cd` would resolve a relative workspace against the distro
        // user's home and run the command there without anyone noticing.
        Assert.Throws<ArgumentException>(() => WslLauncher.Build(Exe, "Ubuntu", "repo", PidFile, Prefix, "true", []));
        Assert.Throws<ArgumentException>(() => WslLauncher.Build(Exe, "Ubuntu", Workspace, "run.pid", Prefix, "true", []));
    }

    [Fact]
    public void BuildPlain_IsExecWithTheArgvAndTheSameEnvironment()
    {
        var launch = WslLauncher.BuildPlain(Exe, "Ubuntu", ["kill", "-TERM", "--", "-1234"]);

        Assert.Equal(Exe, launch.FileName);
        Assert.Equal(["-d", "Ubuntu", "--exec", "kill", "-TERM", "--", "-1234"], launch.Arguments);
        Assert.Equal("1", launch.Environment["WSL_UTF8"]);
        Assert.Equal("1", launch.Environment["WSL_DISABLE_WARNINGS"]);
    }

    [Fact]
    public void BuildPlain_EmptyArgv_IsRejected()
    {
        // `wsl.exe -d D --exec` with nothing after it is invalid usage, not a no-op.
        Assert.Throws<ArgumentException>(() => WslLauncher.BuildPlain(Exe, "Ubuntu", []));
    }
}
