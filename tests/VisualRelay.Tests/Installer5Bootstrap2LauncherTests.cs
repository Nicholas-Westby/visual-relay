using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for bootstrap-2: unified prerequisite gate + nix devshell toolchain.
/// The launcher must re-enter <c>nix develop</c> (once) when nono, uv, or
/// dotnet (unless published) is missing, and the nono gate must run before any
/// backend work.  These must FAIL before the implementation lands.
/// </summary>
public sealed class Installer5Bootstrap2LauncherTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string LauncherPath => Path.Combine(RepoRoot, "visual-relay");

    private static string ReadLauncher() => File.ReadAllText(LauncherPath);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunBashTestAsync(
        string testName, string testBody)
    {
        var script = Path.Combine(Path.GetTempPath(), $"vr-b2-{testName}.sh");
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
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally { try { File.Delete(script); } catch { } }
    }

    /// <summary>Builds a hermetic sandbox, copies the launcher in, sets up
    /// stubs on a crafted PATH, then runs <c>launch</c>.  Assertions run after
    /// the launcher exits.  The generated stub for <c>nix</c> always logs argv
    /// to /tmp/.vr-b2-nix-argv; the backend stub writes a flag to
    /// /tmp/.vr-b2-backend-ran.</summary>
    private static string SetupB2Test(
        bool bypass, bool stubNono, bool stubNix, bool stubUv,
        string? marker, string assertions)
    {
        var no = stubNono ? stub("nono") : "# nono absent";
        var nx = stubNix ? stub("nix", @"printf '%s\n' ""$@"" >> /tmp/.vr-b2-nix-argv") : "# nix absent";
        var uv = stubUv ? stub("uv") : "# uv absent";
        var by = bypass ? "true" : "false";
        var mk = marker is not null ? $"VISUAL_RELAY_NIX_REENTRY={marker}" : "VISUAL_RELAY_NIX_REENTRY=";
        return $$"""
            T=$(mktemp -d); S="$T/bin"; trap 'rm -rf "$T" /tmp/.vr-b2-*' EXIT
            rm -f /tmp/.vr-b2-nix-argv /tmp/.vr-b2-backend-ran
            mkdir -p "$S" "$T/.relay" "$T/tools/backend"
            {{stub("dotnet")}}
            {{no}}
            {{nx}}
            {{uv}}
            cat>"$T/tools/backend/backend.sh"<<'X'&&chmod +x "$T/tools/backend/backend.sh"
            #!/bin/bash
            echo ran>>/tmp/.vr-b2-backend-ran
            exit 0
            X
            echo '{"testCmd":"true","bypassSandbox":{{by}}}'>"$T/.relay/config.json"
            cp "$LAUNCHER" "$T/visual-relay"; chmod +x "$T/visual-relay"
            cd "$T"; RC=0
            PATH="$S:/usr/bin:/bin" {{mk}} bash "$T/visual-relay" launch \
                >/tmp/.vr-b2-out 2>/tmp/.vr-b2-err||RC=$?
            echo "$RC">/tmp/.vr-b2-rc
            {{assertions}}
            """;

        static string stub(string name, string? body = null) =>
            $"cat>\"$S/{name}\"<<'X'&&chmod +x \"$S/{name}\"\n#!/bin/bash\n{body ?? "exit 0"}\nX";
    }

    // ── Static analysis ──────────────────────────────────────────────────

    [Fact]
    public void Launcher_ContainsMissingRequiredTools()
    {
        Assert.Contains("_missing_required_tools", ReadLauncher(), StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_ContainsNixReentryMarkerGuard()
    {
        Assert.Contains("VISUAL_RELAY_NIX_REENTRY", ReadLauncher(), StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_ContainsEnsureDevshell()
    {
        Assert.Contains("_ensure_devshell", ReadLauncher(), StringComparison.Ordinal);
    }

    // ── 1. missing nono + nix available → re-exec via nix develop ────────

    [Fact]
    public async Task Launch_NonoMissing_NixAvailable_ReexecsViaNixDevelop()
    {
        var body = SetupB2Test(bypass: false, stubNono: false, stubNix: true, stubUv: true,
            marker: null, """
            if [[ ! -f /tmp/.vr-b2-nix-argv ]]; then
              echo "FAIL: nix not invoked for missing nono" >&2; exit 1
            fi
            for a in develop '--command' bash launch 'arg with spaces'; do
              grep -qFx -- "$a" /tmp/.vr-b2-nix-argv || { echo "FAIL: '$a' missing" >&2; exit 1; }
            done
            if [[ -f /tmp/.vr-b2-backend-ran ]]; then
              echo "FAIL: backend ran before gate" >&2; exit 1
            fi
            """);
        body = body.Replace(
            "bash \"$T/visual-relay\" launch \\",
            "bash \"$T/visual-relay\" launch 'arg with spaces' \\");
        var (ec, _, err) = await RunBashTestAsync("a-nix-reexec", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 2. bypass:true skips nono only; uv absence still triggers re-entry ─

    /// <summary>bypassSandbox removes the nono requirement, but the unified
    /// gate still checks uv (soft want for launch).  When uv is absent and nix is
    /// available, the gate must trigger re-entry even though nono is bypassed.
    /// This proves bypass is scoped to nono, not a blanket skip of all checks.
    /// </summary>
    [Fact]
    public async Task Launch_BypassSandbox_UvMissing_StillReexecsForUv()
    {
        var body = SetupB2Test(bypass: true, stubNono: false, stubNix: true, stubUv: false,
            marker: null, """
            [[ -f /tmp/.vr-b2-nix-argv ]] || { echo "FAIL: nix not invoked (uv missing should trigger)" >&2; exit 1; }
            grep -qFx develop /tmp/.vr-b2-nix-argv || { echo "FAIL: nix argv missing develop" >&2; exit 1; }
            if [[ -f /tmp/.vr-b2-backend-ran ]]; then
              echo "FAIL: backend ran before gate" >&2; exit 1
            fi
            """);
        var (ec, _, err) = await RunBashTestAsync("b-bypass-uv-nix", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 3. re-entry marker set → no second nix invocation (no loop) ──────

    [Fact]
    public async Task Launch_ReentryMarkerSet_NonoMissing_ExitsWithError_NoLoop()
    {
        var body = SetupB2Test(bypass: false, stubNono: false, stubNix: true, stubUv: true,
            marker: "1", """
            RC=$(cat /tmp/.vr-b2-rc); C=$(cat /tmp/.vr-b2-err /tmp/.vr-b2-out)
            (( RC != 0 )) || { echo "FAIL: should exit non-zero" >&2; exit 1; }
            echo "$C"|grep -qi nono||{ echo "FAIL: no nono mention" >&2; exit 1; }
            if [[ -f /tmp/.vr-b2-nix-argv ]]; then
              echo "FAIL: nix called again (loop)" >&2; exit 1
            fi
            if [[ -f /tmp/.vr-b2-backend-ran ]]; then
              echo "FAIL: backend ran before gate" >&2; exit 1
            fi
            """);
        var (ec, _, err) = await RunBashTestAsync("c-marker-no-loop", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 4. no nix + no nono → exits before backend runs (ordering proof) ─

    [Fact]
    public async Task Launch_NonoMissing_NoNix_ExitsBeforeBackendRuns()
    {
        var body = SetupB2Test(bypass: false, stubNono: false, stubNix: false, stubUv: true,
            marker: null, """
            RC=$(cat /tmp/.vr-b2-rc)
            (( RC != 0 )) || { echo "FAIL: should exit non-zero" >&2; exit 1; }
            if [[ -f /tmp/.vr-b2-backend-ran ]]; then
              echo "FAIL: backend ran before nono gate" >&2; exit 1
            fi
            """);
        var (ec, _, err) = await RunBashTestAsync("d-ordering", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 5. run-task also gets the unified gate ───────────────────────────

    [Fact]
    public async Task RunTask_NonoMissing_NixAvailable_ReexecsViaNixDevelop()
    {
        var body = """
            T=$(mktemp -d); S="$T/bin"; trap 'rm -rf "$T" /tmp/.vr-b2-rt-*' EXIT
            rm -f /tmp/.vr-b2-rt-nix-argv; mkdir -p "$S" "$T/.relay"
            cat>"$S/dotnet"<<'X'&&chmod +x "$S/dotnet"
            #!/bin/bash
            exit 0
            X
            cat>"$S/nix"<<'X'&&chmod +x "$S/nix"
            #!/bin/bash
            printf '%s\n' "$@">>/tmp/.vr-b2-rt-nix-argv
            exit 0
            X
            echo '{"testCmd":"true","bypassSandbox":false}'>"$T/.relay/config.json"
            cp "$LAUNCHER" "$T/visual-relay"; chmod +x "$T/visual-relay"
            cd "$T"
            PATH="$S:/usr/bin:/bin" VISUAL_RELAY_NIX_REENTRY= bash "$T/visual-relay" run-task 'task-id' 2>/dev/null||true
            [[ -f /tmp/.vr-b2-rt-nix-argv ]]||{ echo "FAIL: nix not invoked" >&2;exit 1;}
            for a in develop '--command' bash run-task 'task-id'; do
              grep -qFx -- "$a" /tmp/.vr-b2-rt-nix-argv||{ echo "FAIL: '$a' missing">&2;exit 1;}
            done
            """;
        var (ec, _, err) = await RunBashTestAsync("e-runtask-nix", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    // ── 6. all tools present + nix available → still re-exec via nix develop ─

    /// <summary>
    /// When nix is available and the devshell hasn't been entered yet, the
    /// launcher must unconditionally re-exec into <c>nix develop</c> even when
    /// every tool (dotnet, nono, uv) is already on PATH.  The devshell is the
    /// canonical environment — not just a fallback for missing tools.
    /// </summary>
    [Fact]
    public async Task Launch_AllToolsPresent_StillReexecsViaNixDevelop()
    {
        var body = SetupB2Test(bypass: false, stubNono: true, stubNix: true, stubUv: true,
            marker: null, """
            if [[ ! -f /tmp/.vr-b2-nix-argv ]]; then
              echo "FAIL: nix not invoked when all tools present" >&2; exit 1
            fi
            for a in develop '--command' bash launch; do
              grep -qFx -- "$a" /tmp/.vr-b2-nix-argv || { echo "FAIL: '$a' missing" >&2; exit 1; }
            done
            if [[ -f /tmp/.vr-b2-backend-ran ]]; then
              echo "FAIL: backend ran before gate" >&2; exit 1
            fi
            """);
        var (ec, _, err) = await RunBashTestAsync("f-all-tools-present", body);
        if (!string.IsNullOrEmpty(err))
            Assert.Fail(err);
        Assert.Equal(0, ec);
    }

    private static string ExtractCaseBody(string content, string caseLabel)
    {
        var si = content.IndexOf(caseLabel, StringComparison.Ordinal);
        if (si < 0) return string.Empty;
        var bs = content.IndexOf('\n', si) + 1;
        var te = content.IndexOf(";;", bs, StringComparison.Ordinal);
        return te >= 0 ? content[bs..te] : content[bs..];
    }
}
