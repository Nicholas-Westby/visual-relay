using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for bootstrap-5 (Phase 2): once swival is present, the launcher runs a
/// weekly, consent-gated upgrade check on <c>launch</c>/<c>run</c>.  It must run
/// at most once every 7 days (tracked by a per-machine XDG-state timestamp, NOT
/// the repo tree), always rewrite the timestamp after a check, never block
/// launch, and stay non-fatal on a failing/empty probe.  The "latest" probe is
/// overridable via <c>VISUAL_RELAY_SWIVAL_LATEST_CMD</c>.  These must FAIL before
/// the implementation lands.
/// </summary>
public sealed class Installer5Bootstrap5SwivalUpgradeTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string LauncherPath => Path.Combine(RepoRoot, "visual-relay");
    private static string ReadLauncher() => File.ReadAllText(LauncherPath);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunBashTestAsync(
        string testName, string testBody)
    {
        var script = Path.Combine(Path.GetTempPath(), $"vr-b5-{testName}.sh");
        var escaped = LauncherPath.Replace("'", "'\\''");
        await File.WriteAllTextAsync(script,
            $"#!/usr/bin/env bash\nset -euo pipefail\nLAUNCHER='{escaped}'\n{testBody}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Process? process = null;
        try
        {
            process = new Process();
            process.StartInfo = new ProcessStartInfo("/bin/bash", script)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process.Start();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode,
                await process.StandardOutput.ReadToEndAsync(cts.Token),
                await process.StandardError.ReadToEndAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (Exception) { /* best-effort kill */ }
            throw;
        }
        finally { try { File.Delete(script); } catch (Exception) { /* best-effort temp cleanup */ } }
    }

    private static string Stub(string name, string? body = null) =>
        $"cat>\"$S/{name}\"<<'X'&&chmod +x \"$S/{name}\"\n#!/bin/bash\n{body ?? "exit 0"}\nX";

    /// <summary>Hermetic non-TTY <c>launch</c> with swival present and sandbox
    /// bypassed, an isolated XDG_STATE_HOME, and an overridable "latest" probe.
    /// <paramref name="stampAgeSecs"/> seeds the timestamp file (negative ⇒ no
    /// seed); <paramref name="latest"/> is the stdout the probe emits (non-empty
    /// ⇒ upgrade available); when <paramref name="latestExits"/> is false the
    /// probe records its invocation and exits non-zero (failing probe).</summary>
    private static string SetupLaunch(long stampAgeSecs, string latest, bool latestExits,
        string assertions)
    {
        var seed = stampAgeSecs >= 0
            ? $"echo $(( $(date +%s) - {stampAgeSecs} )) > \"$X/visual-relay/swival-upgrade-check\""
            : "# no seed";
        var probeBody = latestExits
            ? $"echo ran>/tmp/.vr-b5-probe-ran\nprintf '%s' '{latest}'\nexit 0"
            : "echo ran>/tmp/.vr-b5-probe-ran\nexit 3";
        return $$"""
            T=$(mktemp -d); S="$T/bin"; X="$T/xdg-state"; trap 'rm -rf "$T" /tmp/.vr-b5-*' EXIT
            rm -f /tmp/.vr-b5-out /tmp/.vr-b5-err /tmp/.vr-b5-rc /tmp/.vr-b5-probe-ran /tmp/.vr-b5-upgrader-ran /tmp/.vr-b5-backend-ran
            mkdir -p "$S" "$T/.relay" "$T/tools/backend" "$X/visual-relay"
            {{Stub("dotnet")}}
            {{Stub("nono")}}
            {{Stub("brew")}}
            {{Stub("swival", "echo 'swival 1.0.0'\nexit 0")}}
            cat>"$S/vr-swival-probe"<<'X'&&chmod +x "$S/vr-swival-probe"
            #!/bin/bash
            {{probeBody}}
            X
            cat>"$S/vr-swival-upgrader"<<'X'&&chmod +x "$S/vr-swival-upgrader"
            #!/bin/bash
            echo ran>/tmp/.vr-b5-upgrader-ran
            exit 0
            X
            cat>"$T/tools/backend/backend.sh"<<'X'&&chmod +x "$T/tools/backend/backend.sh"
            #!/bin/bash
            echo ran>>/tmp/.vr-b5-backend-ran
            exit 0
            X
            echo '{"testCmd":"true","bypassSandbox":true}'>"$T/.relay/config.json"
            cp "$LAUNCHER" "$T/visual-relay"; chmod +x "$T/visual-relay"
            {{seed}}
            cd "$T"; RC=0
            PATH="$S:/usr/bin:/bin" VISUAL_RELAY_NIX_REENTRY=1 XDG_STATE_HOME="$X" \
                VISUAL_RELAY_SWIVAL_LATEST_CMD="$S/vr-swival-probe" \
                VISUAL_RELAY_SWIVAL_UPGRADER="$S/vr-swival-upgrader" \
                bash "$T/visual-relay" launch </dev/null >/tmp/.vr-b5-out 2>/tmp/.vr-b5-err||RC=$?
            echo "$RC">/tmp/.vr-b5-rc
            STAMP="$X/visual-relay/swival-upgrade-check"
            {{assertions}}
            """;
    }

    // ── Static analysis ──────────────────────────────────────────────────

    [Fact]
    public void Launcher_ContainsUpgradeCheck()
    {
        Assert.Contains("_swival_upgrade_check", ReadLauncher(), StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_ContainsLatestCmdOverride()
    {
        Assert.Contains("VISUAL_RELAY_SWIVAL_LATEST_CMD", ReadLauncher(), StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_ContainsUpgraderOverride()
    {
        Assert.Contains("VISUAL_RELAY_SWIVAL_UPGRADER", ReadLauncher(), StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_UsesXdgStateTimestamp()
    {
        var c = ReadLauncher();
        Assert.Contains("XDG_STATE_HOME", c, StringComparison.Ordinal);
        Assert.Contains("swival-upgrade-check", c, StringComparison.Ordinal);
    }

    // ── 1. Fresh timestamp (<7 days) → no probe, launch proceeds ──────────

    [Fact]
    public async Task Launch_FreshTimestamp_SkipsCheck()
    {
        // 1 day old — well within the 7-day window.
        var body = SetupLaunch(stampAgeSecs: 86400, latest: "swival 2.0.0", latestExits: true, """
            RC=$(cat /tmp/.vr-b5-rc); O=$(cat /tmp/.vr-b5-out /tmp/.vr-b5-err)
            [[ -f /tmp/.vr-b5-probe-ran ]] && { echo "FAIL: probe ran within 7-day window" >&2; echo "$O" >&2; exit 1; } || true
            [[ -f /tmp/.vr-b5-backend-ran ]] || { echo "FAIL: launch did not proceed" >&2; echo "$O" >&2; exit 1; }
            """);
        var (ec, _, err) = await RunBashTestAsync("fresh", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 2. Stale timestamp (>7 days) → probe runs, timestamp rewritten ────

    [Fact]
    public async Task Launch_StaleTimestamp_RunsCheck_RewritesTimestamp()
    {
        // 8 days old.
        var body = SetupLaunch(stampAgeSecs: 8 * 86400, latest: "", latestExits: true, """
            RC=$(cat /tmp/.vr-b5-rc); O=$(cat /tmp/.vr-b5-out /tmp/.vr-b5-err)
            [[ -f /tmp/.vr-b5-probe-ran ]] || { echo "FAIL: probe did not run for stale timestamp" >&2; echo "$O" >&2; exit 1; }
            NOW=$(date +%s); LAST=$(cat "$STAMP")
            (( NOW - LAST < 60 )) || { echo "FAIL: timestamp not rewritten (last=$LAST now=$NOW)" >&2; exit 1; }
            [[ -f /tmp/.vr-b5-backend-ran ]] || { echo "FAIL: launch did not proceed" >&2; echo "$O" >&2; exit 1; }
            """);
        var (ec, _, err) = await RunBashTestAsync("stale", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 3. No timestamp yet → check runs (first launch) ──────────────────

    [Fact]
    public async Task Launch_NoTimestamp_RunsCheck_AndCreatesTimestamp()
    {
        var body = SetupLaunch(stampAgeSecs: -1, latest: "", latestExits: true, """
            RC=$(cat /tmp/.vr-b5-rc); O=$(cat /tmp/.vr-b5-out /tmp/.vr-b5-err)
            [[ -f /tmp/.vr-b5-probe-ran ]] || { echo "FAIL: probe did not run on first launch" >&2; echo "$O" >&2; exit 1; }
            [[ -f "$STAMP" ]] || { echo "FAIL: timestamp not created" >&2; exit 1; }
            [[ -f /tmp/.vr-b5-backend-ran ]] || { echo "FAIL: launch did not proceed" >&2; echo "$O" >&2; exit 1; }
            """);
        var (ec, _, err) = await RunBashTestAsync("none", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 4. Upgrade available + non-TTY → prints, does not upgrade, proceeds ─

    [Fact]
    public async Task Launch_UpgradeAvailable_NonTty_PrintsHint_NoUpgrade_Proceeds()
    {
        var body = SetupLaunch(stampAgeSecs: 8 * 86400, latest: "swival 2.0.0", latestExits: true, """
            RC=$(cat /tmp/.vr-b5-rc); O=$(cat /tmp/.vr-b5-out /tmp/.vr-b5-err)
            (( RC == 0 )) || { echo "FAIL: launch should proceed (rc=$RC)" >&2; echo "$O" >&2; exit 1; }
            echo "$O" | grep -qi 'newer swival is available' || { echo "FAIL: missing upgrade-available hint" >&2; echo "$O" >&2; exit 1; }
            [[ -f /tmp/.vr-b5-upgrader-ran ]] && { echo "FAIL: upgrader ran in non-TTY" >&2; echo "$O" >&2; exit 1; } || true
            echo "$O" | grep -q '\[y/N\]' && { echo "FAIL: prompt in non-TTY" >&2; echo "$O" >&2; exit 1; } || true
            [[ -f /tmp/.vr-b5-backend-ran ]] || { echo "FAIL: launch did not proceed" >&2; echo "$O" >&2; exit 1; }
            """);
        var (ec, _, err) = await RunBashTestAsync("avail-nontty", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 5. Non-fatal: failing probe → launch proceeds, timestamp updates ──

    [Fact]
    public async Task Launch_FailingProbe_NonFatal_Proceeds_TimestampUpdates()
    {
        var body = SetupLaunch(stampAgeSecs: 8 * 86400, latest: "", latestExits: false, """
            RC=$(cat /tmp/.vr-b5-rc); O=$(cat /tmp/.vr-b5-out /tmp/.vr-b5-err)
            (( RC == 0 )) || { echo "FAIL: failing probe broke launch (rc=$RC)" >&2; echo "$O" >&2; exit 1; }
            [[ -f /tmp/.vr-b5-probe-ran ]] || { echo "FAIL: probe did not run" >&2; echo "$O" >&2; exit 1; }
            NOW=$(date +%s); LAST=$(cat "$STAMP")
            (( NOW - LAST < 60 )) || { echo "FAIL: timestamp not updated after failing probe" >&2; exit 1; }
            [[ -f /tmp/.vr-b5-backend-ran ]] || { echo "FAIL: launch did not proceed" >&2; echo "$O" >&2; exit 1; }
            """);
        var (ec, _, err) = await RunBashTestAsync("fail-probe", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }
}
