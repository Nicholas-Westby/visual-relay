using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Execution-backed coverage for the sandboxed shell-verify launch. The
/// argument-shape tests in <see cref="SandboxedTestRunnerArgumentTests"/> only
/// assert the <c>List&lt;string&gt;</c> shape and never run anything. These take
/// the args <see cref="SandboxedTestRunner.ResolveLaunch"/> actually emits, strip
/// the nono prefix, and run the <c>/bin/sh</c> tail through the SAME
/// <see cref="ProcessCapture"/> <c>IEnumerable&lt;string&gt;</c> overload
/// SandboxedTestRunner uses — the overload that adds each entry verbatim, so a
/// merged <c>-c "cmd"</c> entry reaches <c>/bin/sh</c> as one unparseable
/// argument (exit 2). This is the test that would have caught the
/// "sandboxed verify always red" bug; it needs no nono.
/// </summary>
public sealed class SandboxedShellVerifyExecutionTests
{
    [Fact]
    public void ShellMode_SandboxEnabled_FlagAndCommandWithSpacesStaySeparate()
    {
        // The real incident command: spaces AND a flag. -c and the whole command
        // must be two distinct entries, with NO wrapping quotes added.
        var config = TestConfig();
        var sut = new SandboxedTestRunner(new ShellTestRunner(), config);

        var (_, args) = sut.ResolveLaunch("bun test --timeout 15000");

        var dashDash = args.ToList().IndexOf("--");
        Assert.Equal("/bin/sh", args[dashDash + 1]);
        Assert.Equal("-c", args[dashDash + 2]);
        Assert.Equal("bun test --timeout 15000", args[dashDash + 3]);
        Assert.Equal(dashDash + 4, args.Count); // no trailing merged/extra arg
    }

    [Fact]
    public async Task ShellMode_SandboxEnabled_ResolvedShellArgsActuallyExecute()
    {
        // Run the /bin/sh tail of the resolved launch through the exact overload
        // SandboxedTestRunner uses. Merged `-c "cmd"` → /bin/sh exits 2; separate
        // `-c`,`cmd` → exit 0. nono is not needed: we exercise only the /bin/sh
        // sub-launch, which is where the arg-merge bug actually bites.
        var config = TestConfig();
        var sut = new SandboxedTestRunner(new ShellTestRunner(), config);

        var (_, args) = sut.ResolveLaunch("echo vr-sandbox-ok");
        var tail = args.Skip(args.ToList().IndexOf("--") + 1).ToList();

        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            tail[0], tail.Skip(1), Path.GetTempPath(),
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.False(timedOut);
        Assert.Equal(0, exitCode);
        Assert.Contains("vr-sandbox-ok", output);
    }

    private static RelayConfig TestConfig() =>
        new("llm-tasks", "true", "true", [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true, 1, 1, false, true,
            SubagentTimeoutMilliseconds: 5_000,
            TestTimeoutMilliseconds: 300_000,
            FirstOutputTimeoutMsByTier: new Dictionary<string, int>
            { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            FirstOutputTimeoutMs: 660_000,
            MaxStallRetries: 2);
}
