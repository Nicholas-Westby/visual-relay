using System.Text;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Windows runtime probe of the two things that quietly break at the boundary: the
/// exit code (wsl.exe returns the Linux status verbatim, but returns -1 for its own
/// failures, and the envelope's <c>wait</c> has to pass the child's through) and the
/// encoding (wsl.exe's own output is UTF-16 unless <c>WSL_UTF8=1</c>, and a Windows
/// capture decodes with the console code page unless told otherwise).
/// </summary>
public sealed class WslExitCodeAndUtf8Tests
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(2);

    private const int ProbeExitCode = 42;
    private const string NonAscii = "héllo/日本";

    /// <summary>A plain <c>--exec</c> launch: the distro's status, not a wsl.exe failure code.</summary>
    [Fact]
    public async Task PlainLaunch_ExitCode_ComesBackVerbatim()
    {
        var wsl = SkipIfNotOptedIn();

        var (exitCode, output) = await RunAsync(WslLauncher.BuildPlain(
            wsl.WslExePath, wsl.Distro, ["/bin/sh", "-c", "exit 42"]));

        Assert.True(exitCode == ProbeExitCode,
            $"expected {ProbeExitCode}, got {exitCode} (-1 means wsl.exe itself failed): {output}");
    }

    /// <summary>
    /// The production shape: the envelope backgrounds the command through setsid and
    /// returns what <c>wait</c> saw, so a status lost there would be invisible to the
    /// plain launch above.
    /// </summary>
    [Fact]
    public async Task EnvelopeLaunch_ExitCode_SurvivesTheWait()
    {
        var wsl = SkipIfNotOptedIn();
        await RunAsync(WslLauncher.BuildPlain(
            wsl.WslExePath, wsl.Distro, ["mkdir", "-p", WslProcessControl.PidFileDirectory]));
        var pidFile = WslProcessControl.PidFilePath("vr-exit-" + Guid.NewGuid().ToString("N")[..8], "probe");

        var (exitCode, output) = await RunAsync(WslLauncher.Build(
            wsl.WslExePath, wsl.Distro, wsl.DistroHome, pidFile,
            nonoPrefix: [], program: "/bin/sh", args: ["-c", "exit 42"]));

        Assert.True(exitCode == ProbeExitCode,
            $"the envelope returned {exitCode} instead of the child's {ProbeExitCode}: {output}");
    }

    /// <summary>Non-ASCII bytes from the distro survive the relay and the capture's decoding.</summary>
    [Fact]
    public async Task NonAsciiOutput_FromTheDistro_DecodesAsUtf8()
    {
        var wsl = SkipIfNotOptedIn();

        var (exitCode, output) = await RunAsync(WslLauncher.BuildPlain(
            wsl.WslExePath, wsl.Distro, ["printf", @"%s\n", NonAscii]));

        Assert.Equal(0, exitCode);
        var received = output.TrimEnd('\r', '\n');
        Assert.True(NonAscii == received,
            $"expected {Hex(NonAscii)} ({NonAscii}), received {Hex(received)} ({received})");
    }

    /// <summary>
    /// wsl.exe's OWN output (not a Linux command's) is UTF-16LE without a BOM unless
    /// <c>WSL_UTF8=1</c> is set, which every VR launch sets: the distro listing must
    /// parse back into the distro the context resolved.
    /// </summary>
    [Fact]
    public async Task DistroListing_UnderTheWslUtf8Variable_ParsesBack()
    {
        var wsl = SkipIfNotOptedIn();

        var (exitCode, listing, timedOut) = await ProcessCapture.RunAsync(
            wsl.WslExePath, ["-l", "-v"], Path.GetTempPath(), StepTimeout,
            CancellationToken.None, environment: WslExeEnvironment.Variables);

        Assert.False(timedOut);
        Assert.Equal(0, exitCode);
        var distros = WslListParser.Parse(listing);
        Assert.Contains(distros, d => d.Name == wsl.Distro && d.Version == 2);
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
        var wsl = WslContextResolver.TryGetCurrent();
        Assert.SkipUnless(wsl is not null, "no usable WSL2 distro with nono on this host");
        return wsl!;
    }

    private static string Hex(string text) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(text)).ToLowerInvariant();

    private static async Task<(int ExitCode, string Output)> RunAsync(WslLaunch launch)
    {
        var (exitCode, captured, timedOut) = await ProcessCapture.RunAsync(
            launch.FileName, launch.Arguments, Path.GetTempPath(), StepTimeout,
            CancellationToken.None, environment: launch.Environment);
        Assert.False(timedOut, $"wsl.exe did not finish within {StepTimeout.TotalSeconds:F0}s: {captured}");
        return (exitCode, captured);
    }
}
