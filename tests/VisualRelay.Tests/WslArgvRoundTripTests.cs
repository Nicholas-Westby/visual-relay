using System.Text;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Windows runtime probe: what the Linux child ACTUALLY RECEIVED. The argv unit
/// tests pin the array VR builds; only a real run proves the two encoders between
/// VR and the child (.NET's <c>ArgumentList</c> quoting, then wsl.exe's
/// <c>CommandLineToArgvW</c> split) hand it back unchanged. The receiver is
/// <c>printf '%s\0'</c>, which repeats its format once per argument, so every
/// argument arrives followed by a NUL byte that no quoting could have produced.
/// <para>Skipped everywhere but an opted-in Windows host with a usable WSL2 distro
/// (<see cref="NonoIntegration.ThisMachinesWsl"/>); on macOS it reports the platform.</para>
/// </summary>
public sealed class WslArgvRoundTripTests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The spec's hostile set: a space, both quote flavours, a backslash, a dollar,
    /// a backtick, shell metacharacters, non-ASCII, and an empty argument. Each must
    /// arrive as exactly one argument, byte for byte.
    /// </summary>
    private static readonly string[] HostileArguments =
    [
        "plain",
        "a b",
        "c\"d",
        "e'f",
        @"g\h",
        "$i",
        "`j`",
        "k;l|m&n",
        "héllo/日本",
        "",
    ];

    /// <summary>A plain <c>--exec</c> launch: no envelope, no sandbox, just the two encoders.</summary>
    [Fact]
    public async Task PlainLaunch_HostileArguments_ReachTheReceiverVerbatim()
    {
        var wsl = SkipIfNotOptedIn();
        var launch = WslLauncher.BuildPlain(
            wsl.WslExePath, wsl.Distro, ["printf", @"%s\0", .. HostileArguments]);

        var (exitCode, captured) = await RunAsync(launch);

        Assert.Equal(0, exitCode);
        AssertReceived(HostileArguments, captured);
    }

    /// <summary>
    /// The production shape: the same arguments through <see cref="WslLauncher.Build"/>,
    /// so they also cross the envelope's <c>setsid "$@"</c>. A shell that re-split or
    /// re-quoted a positional parameter would show up here and nowhere else.
    /// </summary>
    [Fact]
    public async Task EnvelopeLaunch_HostileArguments_SurviveSetsidAndTheEnvelope()
    {
        var wsl = SkipIfNotOptedIn();
        await RunAsync(WslLauncher.BuildPlain(
            wsl.WslExePath, wsl.Distro, ["mkdir", "-p", WslProcessControl.PidFileDirectory]));
        var pidFile = WslProcessControl.PidFilePath("vr-argv-" + Guid.NewGuid().ToString("N")[..8], "probe");

        var launch = WslLauncher.Build(
            wsl.WslExePath, wsl.Distro, wsl.DistroHome, pidFile,
            nonoPrefix: [], program: "printf", args: [@"%s\0", .. HostileArguments]);
        var (exitCode, captured) = await RunAsync(launch);

        Assert.Equal(0, exitCode);
        AssertReceived(HostileArguments, captured);
    }

    /// <summary>
    /// Arguments carrying newlines, asserted as bytes: the capture reads whole lines,
    /// so the receiver hexdumps what it got instead of printing it. The arguments are
    /// the shell's positional parameters (the envelope's shape), so nothing re-parses
    /// them on the way in.
    /// </summary>
    [Fact]
    public async Task PlainLaunch_ArgumentsWithNewlines_ArriveByteForByte()
    {
        var wsl = SkipIfNotOptedIn();
        string[] arguments = ["one\ntwo", "trailing\n", "héllo\n日本"];
        var launch = WslLauncher.BuildPlain(wsl.WslExePath, wsl.Distro,
            ["/bin/sh", "-c", "printf '%s\\0' \"$@\" | od -An -v -tx1", "vr", .. arguments]);

        var (exitCode, captured) = await RunAsync(launch);

        Assert.Equal(0, exitCode);
        var expected = Hex(string.Join('\0', arguments) + '\0');
        var received = new string(captured.Where(char.IsAsciiHexDigit).ToArray());
        Assert.True(expected == received,
            $"the receiver got different bytes\nexpected: {expected}\nreceived: {received}");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// The probe gate: the platform, the opt-in marker the sandbox-skip guards
    /// recognise by name, and a usable WSL2 distro with nono (what the WSL gate
    /// checks before a launch). Bare-named like <c>NonoRealBuildTests</c>'s helper so
    /// every call site carries the recognised opt-out token.
    /// </summary>
    private static WslContext SkipIfNotOptedIn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the WSL sandbox is the Windows arm");
        NonoIntegration.SkipIfNotOptedIn("VR_RUN_NONO_INTEGRATION=1 required for the real WSL probes.");
        var wsl = NonoIntegration.ThisMachinesWsl();
        Assert.SkipUnless(wsl is not null, "no usable WSL2 distro with nono on this host");
        return wsl!;
    }

    /// <summary>
    /// The NUL-separated fields the receiver printed, compared element by element.
    /// printf terminates the last argument too, so the split leaves one empty tail.
    /// </summary>
    private static void AssertReceived(IReadOnlyList<string> expected, string captured)
    {
        // The capture re-terminates the one line it read; the NULs inside it are untouched.
        var fields = captured.TrimEnd('\r', '\n').Split('\0');
        Assert.True(fields.Length == expected.Count + 1,
            $"expected {expected.Count} NUL-terminated fields, got {fields.Length - 1}: {Hex(captured)}");
        Assert.Equal(string.Empty, fields[^1]);
        Assert.Equal(expected, fields[..^1]);
    }

    private static string Hex(string text) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant();

    private static async Task<(int ExitCode, string Output)> RunAsync(WslLaunch launch)
    {
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            launch.FileName, launch.Arguments, Path.GetTempPath(), StepTimeout,
            CancellationToken.None, environment: launch.Environment);
        Assert.False(timedOut, $"wsl.exe did not finish within {StepTimeout.TotalSeconds:F0}s: {output}");
        return (exitCode, output);
    }
}
