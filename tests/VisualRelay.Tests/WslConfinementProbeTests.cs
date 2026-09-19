using System.Diagnostics;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// Windows runtime probe: the spec's confinement verdict table, run through the real
/// nono inside the distro with the real <c>vr-guard</c> profile the production
/// ensurer places there. The same eight rows macOS produces — workspace write,
/// workspace delete, git, a toolchain cache write, <c>~/Documents</c> write denied,
/// <c>~/.ssh</c> read denied, network reachable, <c>/usr/bin</c> readable — are run
/// once on the distro's own filesystem and once on a DrvFs (<c>/mnt/c</c>) workspace,
/// with each row's wall time recorded. The <c>~/.ssh</c> row is the one the deleted
/// Windows sandbox could not enforce.
/// </summary>
/// <param name="output">Carries the verdict table and the timings into the test output the probe workflow uploads.</param>
public sealed class WslConfinementProbeTests(ITestOutputHelper output)
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Unsandboxed preparation, so every later denial is attributable to nono and not
    /// to a missing directory: the workspace is a git repository, <c>~/Documents</c>
    /// exists, and <c>~/.ssh</c> holds a readable probe file. Its own success is the
    /// control — these paths ARE writable and readable without the sandbox.
    /// </summary>
    private const string SetupScript =
        "set -e; mkdir -p \"$1\" \"$HOME/Documents\" \"$HOME/.ssh\"; cd \"$1\"; git init -q .; "
        + "printf 'probe\\n' > \"$HOME/.ssh/vr-probe-key\"; chmod 600 \"$HOME/.ssh/vr-probe-key\"; "
        + "cat \"$HOME/.ssh/vr-probe-key\" > /dev/null; touch \"$HOME/Documents/vr-probe-control\"; "
        + "rm -f \"$HOME/Documents/vr-probe-control\"";

    private const string CleanupScript =
        "rm -rf \"$1\"; rm -f \"$HOME/.ssh/vr-probe-key\" \"$HOME/Documents/vr-probe\" \"$HOME/.npm/vr-probe\"";

    /// <summary>The spec's table: what each command does and whether the sandbox must let it.</summary>
    private static readonly (string Name, string Script, bool Allowed)[] Rows =
    [
        ("workspace write", "echo ok > vr-probe.txt && test -f vr-probe.txt", true),
        ("workspace delete", "echo ok > vr-del.txt && rm vr-del.txt && test ! -e vr-del.txt", true),
        ("git status", "git status --short", true),
        ("toolchain cache write", "mkdir -p \"$HOME/.npm\" && echo x > \"$HOME/.npm/vr-probe\"", true),
        ("documents write", "touch \"$HOME/Documents/vr-probe\"", false),
        ("ssh read", "cat \"$HOME/.ssh/vr-probe-key\"", false),
        ("network", "curl -sSI -m 30 https://api.github.com > /dev/null", true),
        ("usr bin read", "ls /usr/bin > /dev/null", true),
    ];

    /// <summary>The parity case: on the distro's own filesystem the verdicts must be macOS's.</summary>
    [Fact]
    public async Task WorkspaceOnTheDistroFilesystem_ProducesTheSpecVerdicts()
    {
        var wsl = SkipIfNotOptedIn();
        var workspace = $"{wsl.DistroHome.TrimEnd('/')}/vr-probe-{Guid.NewGuid().ToString("N")[..8]}";

        await ProbeAsync(wsl, workspace, "ext4",
            "these are the verdicts the sandbox promises on every platform");
    }

    /// <summary>
    /// The measurement behind <see cref="WslWorkspacePolicy.MntPolicy"/>: whether
    /// Landlock enforces the same table across the DrvFs boundary, and what the
    /// boundary costs. A failure here keeps the refusal of <c>/mnt</c> workspaces;
    /// a pass (with acceptable timings) is what would downgrade it to a warning.
    /// </summary>
    [Fact]
    public async Task WorkspaceOnDrvFs_ProducesTheSpecVerdicts()
    {
        var wsl = SkipIfNotOptedIn();
        var windowsWorkspace = Path.Combine(Path.GetTempPath(), "vr-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.True(WslPath.TryDriveToMnt(windowsWorkspace, out var workspace),
            $"'{windowsWorkspace}' is not a drive path this probe can reach through DrvFs");

        await ProbeAsync(wsl, workspace, "drvfs",
            "a failure here is the finding that keeps DrvFs workspaces refused");
    }

    // ── The probe ──────────────────────────────────────────────────────

    private async Task ProbeAsync(WslContext wsl, string workspace, string label, string consequence)
    {
        await RequireDistroToolsAsync(wsl);
        var profile = await NonoProfileEnsurer.EnsureAsync();
        await RunAsync(Plain(wsl, ["mkdir", "-p", WslProcessControl.PidFileDirectory]));
        var pidFile = WslProcessControl.PidFilePath("vr-probe-" + Guid.NewGuid().ToString("N")[..8], label);

        var (setupExit, setupOutput) = await RunAsync(Plain(wsl, ["/bin/sh", "-c", SetupScript, "vr", workspace]));
        Assert.True(setupExit == 0, $"the unsandboxed setup of '{workspace}' failed: {setupOutput}");
        try
        {
            var failures = new List<string>();
            output.WriteLine($"{label}: workspace {workspace}, profile {profile}, nono {wsl.NonoPath}");
            foreach (var row in Rows)
            {
                var (allowed, exitCode, elapsed, rowOutput) = await RunRowAsync(wsl, workspace, pidFile, profile, row.Script);
                output.WriteLine($"{label} | {row.Name} | {Verdict(allowed)} | exit {exitCode} | {elapsed} ms");
                if (allowed != row.Allowed)
                {
                    failures.Add(
                        $"{row.Name}: expected {Verdict(row.Allowed)}, got {Verdict(allowed)} (exit {exitCode}) {Tail(rowOutput)}");
                }
            }

            Assert.True(failures.Count == 0,
                $"the {label} confinement probe disagreed with the spec ({consequence}):\n"
                + string.Join("\n", failures));
        }
        finally
        {
            await RunAsync(Plain(wsl, ["/bin/sh", "-c", CleanupScript, "vr", workspace]));
        }
    }

    /// <summary>One row: the production launch shape, timed end to end.</summary>
    private static async Task<(bool Allowed, int ExitCode, long Elapsed, string Output)> RunRowAsync(
        WslContext wsl, string workspace, string pidFile, string profile, string script)
    {
        var launch = WslLauncher.Build(
            wsl.WslExePath, wsl.Distro, workspace, pidFile,
            [wsl.NonoPath, "run", "--profile", profile, "--allow-cwd", "--silent", "--"],
            "/bin/sh", ["-c", script]);

        var timer = Stopwatch.StartNew();
        var (exitCode, rowOutput) = await RunAsync(launch);
        timer.Stop();
        return (exitCode == 0, exitCode, timer.ElapsedMilliseconds, rowOutput);
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

    /// <summary>Two rows of the table need git and curl inside the distro; without them the probe proves nothing.</summary>
    private static async Task RequireDistroToolsAsync(WslContext wsl)
    {
        var (exitCode, _) = await RunAsync(Plain(wsl,
            ["/bin/sh", "-lc", "command -v git > /dev/null && command -v curl > /dev/null"]));
        Assert.SkipUnless(exitCode == 0, "git and curl must be installed inside the distro for this probe");
    }

    private static WslLaunch Plain(WslContext wsl, IReadOnlyList<string> argv) =>
        WslLauncher.BuildPlain(wsl.WslExePath, wsl.Distro, argv);

    private static string Verdict(bool allowed) => allowed ? "allowed" : "denied";

    private static string Tail(string text) =>
        text.Length <= 400 ? text.Trim() : "…" + text[^400..].Trim();

    private static async Task<(int ExitCode, string Output)> RunAsync(WslLaunch launch)
    {
        var (exitCode, captured, timedOut) = await ProcessCapture.RunAsync(
            launch.FileName, launch.Arguments, Path.GetTempPath(), StepTimeout,
            CancellationToken.None, environment: launch.Environment);
        Assert.False(timedOut, $"wsl.exe did not finish within {StepTimeout.TotalSeconds:F0}s: {captured}");
        return (exitCode, captured);
    }
}
