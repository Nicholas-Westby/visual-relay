using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for bootstrap-4 (Phase 1): the launcher must treat <c>swival</c> as a
/// hard, always-required tool (not sandbox-gated like nono).  When swival is
/// missing it must offer a consent-gated install via the Homebrew tap
/// (overridable through <c>VISUAL_RELAY_SWIVAL_INSTALLER</c>); in a non-TTY
/// context it must print install instructions, NOT run the installer, and exit
/// non-zero — before the backend/app starts.  These must FAIL before the
/// implementation lands.
/// </summary>
public sealed class Installer5Bootstrap4SwivalInstallTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string LauncherPath => Path.Combine(RepoRoot, "visual-relay");
    private static string ReadLauncher() => File.ReadAllText(LauncherPath);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunBashTestAsync(
        string testName, string testBody)
    {
        var script = Path.Combine(Path.GetTempPath(), $"vr-b4-{testName}.sh");
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

    /// <summary>Hermetic non-TTY <c>launch</c>: stubs dotnet/nono/nix/backend on
    /// a crafted PATH; swival is optionally stubbed.  Re-entry marker is set so
    /// the nix devshell re-exec is skipped and per-tool gates fire directly.
    /// VISUAL_RELAY_SWIVAL_INSTALLER points at a stub that records when it runs.
    /// Sandbox is bypassed so this test isolates the swival gate from nono.</summary>
    private static string SetupLaunch(bool stubSwival, bool setInstaller, string assertions)
    {
        var sw = stubSwival ? Stub("swival") : "# swival absent";
        var inst = setInstaller
            ? "cat>\"$S/vr-swival-installer\"<<'X'&&chmod +x \"$S/vr-swival-installer\"\n#!/bin/bash\necho ran>/tmp/.vr-b4-installer-ran\nexit 0\nX"
            : "# no installer stub";
        var instEnv = setInstaller
            ? "VISUAL_RELAY_SWIVAL_INSTALLER=\"$S/vr-swival-installer\""
            : "";
        return $$"""
            T=$(mktemp -d); S="$T/bin"; trap 'rm -rf "$T" /tmp/.vr-b4-*' EXIT
            rm -f /tmp/.vr-b4-out /tmp/.vr-b4-err /tmp/.vr-b4-rc /tmp/.vr-b4-installer-ran /tmp/.vr-b4-backend-ran
            mkdir -p "$S" "$T/.relay" "$T/tools/backend"
            {{Stub("dotnet")}}
            {{Stub("nono")}}
            {{Stub("brew")}}
            {{sw}}
            {{inst}}
            cat>"$T/tools/backend/backend.sh"<<'X'&&chmod +x "$T/tools/backend/backend.sh"
            #!/bin/bash
            echo ran>>/tmp/.vr-b4-backend-ran
            exit 0
            X
            echo '{"testCmd":"true","bypassSandbox":true}'>"$T/.relay/config.json"
            cp "$LAUNCHER" "$T/visual-relay"; chmod +x "$T/visual-relay"
            cd "$T"; RC=0
            PATH="$S:/usr/bin:/bin" VISUAL_RELAY_NIX_REENTRY=1 {{instEnv}} \
                XDG_STATE_HOME="$T/state" VISUAL_RELAY_SWIVAL_LATEST_CMD=true \
                bash "$T/visual-relay" launch </dev/null >/tmp/.vr-b4-out 2>/tmp/.vr-b4-err||RC=$?
            echo "$RC">/tmp/.vr-b4-rc
            {{assertions}}
            """;
    }

    // ── Static analysis ──────────────────────────────────────────────────

    [Fact]
    public void Launcher_ContainsRequireSwival()
    {
        Assert.Contains("_require_swival", ReadLauncher(), StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_ContainsOfferSwivalInstall()
    {
        Assert.Contains("_offer_swival_install", ReadLauncher(), StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_ContainsSwivalInstallerOverride()
    {
        Assert.Contains("VISUAL_RELAY_SWIVAL_INSTALLER", ReadLauncher(), StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_ContainsSwivalTapInstallCommand()
    {
        Assert.Contains("swival/tap/swival", ReadLauncher(), StringComparison.Ordinal);
    }

    // ── 1. swival present → no install offer; launch proceeds ─────────────

    [Fact]
    public async Task Launch_SwivalPresent_NoOffer_LaunchProceeds()
    {
        var body = SetupLaunch(stubSwival: true, setInstaller: true, """
            RC=$(cat /tmp/.vr-b4-rc); O=$(cat /tmp/.vr-b4-out /tmp/.vr-b4-err)
            [[ -f /tmp/.vr-b4-installer-ran ]] && { echo "FAIL: installer ran with swival present" >&2; echo "$O" >&2; exit 1; } || true
            echo "$O" | grep -qi 'swival.*install' && { echo "FAIL: install hint with swival present" >&2; echo "$O" >&2; exit 1; } || true
            [[ -f /tmp/.vr-b4-backend-ran ]] || { echo "FAIL: backend did not run (launch should proceed)" >&2; echo "$O" >&2; exit 1; }
            """);
        var (ec, _, err) = await RunBashTestAsync("present", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 2. swival missing + non-TTY → instructions, no installer, exit ≠0 ─

    [Fact]
    public async Task Launch_SwivalMissing_NonTty_PrintsInstructions_NoInstaller_ExitsNonZero()
    {
        var body = SetupLaunch(stubSwival: false, setInstaller: true, """
            RC=$(cat /tmp/.vr-b4-rc); O=$(cat /tmp/.vr-b4-out /tmp/.vr-b4-err)
            (( RC != 0 )) || { echo "FAIL: should exit non-zero" >&2; echo "$O" >&2; exit 1; }
            [[ -f /tmp/.vr-b4-installer-ran ]] && { echo "FAIL: installer ran in non-TTY" >&2; echo "$O" >&2; exit 1; } || true
            echo "$O" | grep -q 'swival/tap/swival' || { echo "FAIL: missing brew install instructions" >&2; echo "$O" >&2; exit 1; }
            echo "$O" | grep -q '\[y/N\]' && { echo "FAIL: prompt in non-TTY" >&2; echo "$O" >&2; exit 1; } || true
            [[ -f /tmp/.vr-b4-backend-ran ]] && { echo "FAIL: backend ran before swival gate" >&2; echo "$O" >&2; exit 1; } || true
            """);
        var (ec, _, err) = await RunBashTestAsync("missing-nontty", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 3. TTY yes → installer override runs ──────────────────────────────

    /// <summary>Drives the TTY "yes" path the way the bootstrap-3 suite drives
    /// VISUAL_RELAY_NIX_INSTALLER: a Python pty feeds 'y', and the swival
    /// installer override stub records that it ran and then itself drops a swival
    /// stub on PATH so the post-install re-check finds it and launch proceeds.</summary>
    [Fact]
    public async Task Launch_SwivalMissing_TtyYes_InstallerOverrideRuns()
    {
        var body = """
            T=$(mktemp -d); S="$T/bin"; trap 'rm -rf "$T" /tmp/.vr-b4y-*' EXIT
            rm -f /tmp/.vr-b4y-*; mkdir -p "$S" "$T/.relay" "$T/tools/backend"
            cat>"$S/dotnet"<<'X'&&chmod +x "$S/dotnet"
            #!/bin/bash
            exit 0
            X
            cat>"$S/nono"<<'X'&&chmod +x "$S/nono"
            #!/bin/bash
            exit 0
            X
            cat>"$S/brew"<<'X'&&chmod +x "$S/brew"
            #!/bin/bash
            exit 0
            X
            cat>"$S/vr-swival-installer"<<'X'&&chmod +x "$S/vr-swival-installer"
            #!/bin/bash
            echo ran>/tmp/.vr-b4y-installer-ran
            cat>"REPLACE_S/swival"<<'Y'&&chmod +x "REPLACE_S/swival"
            #!/bin/bash
            exit 0
            Y
            exit 0
            X
            sed -i.bak 's|REPLACE_S|'"$S"'|g' "$S/vr-swival-installer" && rm -f "$S/vr-swival-installer.bak"
            cat>"$T/tools/backend/backend.sh"<<'X'&&chmod +x "$T/tools/backend/backend.sh"
            #!/bin/bash
            echo ran>>/tmp/.vr-b4y-backend-ran
            exit 0
            X
            echo '{"testCmd":"true","bypassSandbox":true}'>"$T/.relay/config.json"
            cp "$LAUNCHER" "$T/visual-relay"; chmod +x "$T/visual-relay"
            cd "$T"
            cat>"$T/run.sh"<<"REOF"
            #!/bin/bash
            export PATH="REPLACE_S:/usr/bin:/bin"
            export VISUAL_RELAY_NIX_REENTRY=1
            export VISUAL_RELAY_SWIVAL_INSTALLER="REPLACE_S/vr-swival-installer"
            export XDG_STATE_HOME="REPLACE_T/state"
            export VISUAL_RELAY_SWIVAL_LATEST_CMD=true
            exec bash "REPLACE_T/visual-relay" launch
            REOF
            sed -e 's|REPLACE_T|'"$T"'|g' -e 's|REPLACE_S|'"$S"'|g' "$T/run.sh">"$T/runf.sh"
            chmod +x "$T/runf.sh"
            python3 -c "
            import os,pty,sys
            d=sys.stdin.buffer.read();m,s=pty.openpty();p=os.fork()
            if p==0:
             os.close(m)
             for f in(0,1,2):os.dup2(s,f)
             os.close(s);os.execv('$T/runf.sh',['runf.sh'])
            o=b'';os.close(s)
            while d:
             try:n=os.write(m,d);d=d[n:]
             except:break
            while 1:
             try:x=os.read(m,4096)
             except:break
             if not x:break
             o+=x
            os.close(m);_,st=os.waitpid(p,0)
            rc=os.waitstatus_to_exitcode(st)
            with open('/tmp/.vr-b4y-out','wb') as f:f.write(o)
            with open('/tmp/.vr-b4y-rc','w') as f:f.write(str(rc))
            "<<'PYEOF'
            y
            PYEOF
            O=$(cat /tmp/.vr-b4y-out)
            [[ -f /tmp/.vr-b4y-installer-ran ]] || { echo "FAIL: swival installer override not invoked" >&2; echo "$O">&2; exit 1; }
            [[ -f /tmp/.vr-b4y-backend-ran ]] || { echo "FAIL: backend did not run after install" >&2; echo "$O">&2; exit 1; }
            """;
        var (ec, _, err) = await RunBashTestAsync("tty-yes", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }
}
