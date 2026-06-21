using System.Diagnostics;
namespace VisualRelay.Tests;

public sealed class Installer5Bootstrap3LauncherTests
{
    static string R => RepoSetup.Root;
    static string L => Path.Combine(R, "visual-relay");
    static string C() => File.ReadAllText(L);
    static async Task<(int EC, string Out, string Err)> Run(string name, string body)
    {
        var s = Path.Combine(Path.GetTempPath(), $"vr-b3-{name}.sh");
        await File.WriteAllTextAsync(s,
            $"#!/usr/bin/env bash\nset -euo pipefail\nLAUNCHER='{L.Replace("'", "'\\''")}'\n{body}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(s, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var p = new Process
        {
            StartInfo = new("/bin/bash", s)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }
        };
        p.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await p.WaitForExitAsync(cts.Token);
        try { return (p.ExitCode, await p.StandardOutput.ReadToEndAsync(cts.Token), await p.StandardError.ReadToEndAsync(cts.Token)); }
        finally { try { File.Delete(s); } catch (Exception) { /* best-effort temp cleanup */ } }
    }
    static string Stub(string name, string? body = null) =>
        $"cat>\"$S/{name}\"<<'X'&&chmod +x \"$S/{name}\"\n#!/bin/bash\n{body ?? "exit 0"}\nX";
    // Hermetic TTY test: runs launcher in a Python pty with stdin fed in.
    // isBody = installer stub body (null = no stub).
    static string TtyTest(string stdin, bool nono, bool nix, bool dotnet,
        bool bypass, bool reentry, string? isBody, string assert)
    {
        var no = nono ? Stub("nono") : "# nono absent";
        var nx = nix ? Stub("nix", @"printf '%s\n' ""$@"" >> /tmp/.vr-b3-nix-argv") : "# nix absent";
        var dt = dotnet ? Stub("dotnet") : "# dotnet absent";
        var bp = bypass ? "true" : "false";
        var ie = isBody is not null
            ? "export VISUAL_RELAY_NIX_INSTALLER=REPLACE_S/vr-nix-installer" : ":";
        var re = reentry ? "export VISUAL_RELAY_NIX_REENTRY=1" : "export VISUAL_RELAY_NIX_REENTRY=";
        var fn = !nix ? "export _VISUAL_RELAY_FAKE_NO_NIX=1" : ":";
        var ins = isBody is not null
            ? $"cat>\"$S/vr-nix-installer\"<<'X'&&chmod +x \"$S/vr-nix-installer\"\n#!/bin/bash\n{isBody}\nX"
            : "# no installer stub";
        var input = stdin.Length > 0 ? stdin + "\n" : "\n";
        return $$"""
            T=$(mktemp -d); S="$T/bin"; trap 'rm -rf "$T" /tmp/.vr-b3-*' EXIT
            rm -f /tmp/.vr-b3-nix-argv /tmp/.vr-b3-installer-ran /tmp/.vr-b3-out /tmp/.vr-b3-rc
            mkdir -p "$S" "$T/.relay"
            {{dt}}
            {{no}}
            {{nx}}
            {{ins}}
            echo '{"testCmd":"true","bypassSandbox":{{bp}}}' > "$T/.relay/config.json"
            cp "$LAUNCHER" "$T/visual-relay"; chmod +x "$T/visual-relay"
            cd "$T"
            cat>"$T/run.sh"<<'REOF'
            #!/bin/bash
            export PATH="REPLACE_S:/usr/bin:/bin"
            REPLACE_IE
            REPLACE_RE
            REPLACE_FN
            exec bash "REPLACE_T/visual-relay" run-task test-id
            REOF
            sed -e 's|REPLACE_T|'"$T"'|g' \
                -e 's|REPLACE_IE|{{ie}}|g' -e 's|REPLACE_RE|{{re}}|g' \
                -e 's|REPLACE_FN|{{fn}}|g' \
                -e 's|REPLACE_S|'"$S"'|g' "$T/run.sh">"$T/run-final.sh"
            chmod +x "$T/run-final.sh"
            python3 -c "
            import os,pty,sys
            d=sys.stdin.buffer.read();m,s=pty.openpty();p=os.fork()
            if p==0:
             os.close(m)
             for f in(0,1,2):os.dup2(s,f)
             os.close(s);os.execv('$T/run-final.sh',['run-final.sh'])
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
            with open('/tmp/.vr-b3-out','wb') as f:f.write(o)
            with open('/tmp/.vr-b3-err','wb') as f:f.write(b'')
            with open('/tmp/.vr-b3-rc','w') as f:f.write(str(rc))
            "<<'PYEOF'
            {{input}}
            PYEOF
            {{assert}}
            """;
    }
    // Non-TTY variant: stdin from /dev/null, stdout/stderr separated.
    static string NonTtyTest(bool nono, bool nix, bool dotnet, bool bypass,
        bool reentry, string? isBody, string assert)
    {
        var no = nono ? Stub("nono") : "# nono absent";
        var nx = nix ? Stub("nix", @"printf '%s\n' ""$@"" >> /tmp/.vr-b3-nix-argv") : "# nix absent";
        var dt = dotnet ? Stub("dotnet") : "# dotnet absent";
        var bp = bypass ? "true" : "false";
        var ie = isBody is not null ? "VISUAL_RELAY_NIX_INSTALLER=\"$S/vr-nix-installer\"" : "";
        var re = reentry ? "VISUAL_RELAY_NIX_REENTRY=1" : "VISUAL_RELAY_NIX_REENTRY=";
        var fn = !nix ? "_VISUAL_RELAY_FAKE_NO_NIX=1" : "";
        var ins = isBody is not null
            ? $"cat>\"$S/vr-nix-installer\"<<'X'&&chmod +x \"$S/vr-nix-installer\"\n#!/bin/bash\n{isBody}\nX"
            : "# no installer stub";
        return $$"""
            T=$(mktemp -d); S="$T/bin"; trap 'rm -rf "$T" /tmp/.vr-b3-*' EXIT
            rm -f /tmp/.vr-b3-nix-argv /tmp/.vr-b3-installer-ran /tmp/.vr-b3-out /tmp/.vr-b3-rc
            mkdir -p "$S" "$T/.relay"
            {{dt}}
            {{no}}
            {{nx}}
            {{ins}}
            echo '{"testCmd":"true","bypassSandbox":{{bp}}}' > "$T/.relay/config.json"
            cp "$LAUNCHER" "$T/visual-relay"; chmod +x "$T/visual-relay"
            cd "$T"
            RC=0
            PATH="$S:/usr/bin:/bin" {{ie}} {{re}} {{fn}} bash "$T/visual-relay" run-task test-id \
              </dev/null >/tmp/.vr-b3-out 2>/tmp/.vr-b3-err || RC=$?
            echo "$RC" > /tmp/.vr-b3-rc
            {{assert}}
            """;
    }
    // ═══ Static analysis ═══
    [Fact] public void Has_FindNix() => Assert.Contains("_find_nix", C(), StringComparison.Ordinal);
    [Fact]
    public void Has_OfferNixInstall()
    { Assert.Contains("_offer_nix_install", C(), StringComparison.Ordinal); Assert.Contains("VISUAL_RELAY_NIX_INSTALLER", C(), StringComparison.Ordinal); }
    [Fact]
    public void ChecksTty()
    { Assert.Contains("-t 0", C(), StringComparison.Ordinal); Assert.Contains("-t 1", C(), StringComparison.Ordinal); }
    [Fact]
    public void HasManualCurlHint()
    { Assert.Contains("install.determinate.systems", C(), StringComparison.Ordinal); Assert.Contains("curl", C(), StringComparison.Ordinal); }
    [Fact]
    public void EnsureDevshell_CallsOffer()
    { Assert.Contains("_offer_nix_install", ExtractFn(C(), "_ensure_devshell"), StringComparison.Ordinal); }
    // ═══ TTY: y → install + re-exec ═══
    [Fact]
    public async Task Tty_Yes_InstallerInvokedAndReexecs()
    {
        var body = TtyTest("y", nono: false, nix: false, dotnet: true,
            bypass: false, reentry: false, isBody: """
            echo ran > /tmp/.vr-b3-installer-ran
            D=$(dirname "$0")
            cat>"$D/nix"<<'Y'&&chmod +x "$D/nix"
            #!/bin/bash
            printf '%s\n' "$@" >> /tmp/.vr-b3-nix-argv
            exit 0
            Y
            exit 0
            """, assert: """
            RC=$(cat /tmp/.vr-b3-rc); O=$(cat /tmp/.vr-b3-out)
            [[ -f /tmp/.vr-b3-installer-ran ]] || { echo FAIL: installer not invoked >&2; echo "$O" >&2; exit 1; }
            [[ -f /tmp/.vr-b3-nix-argv ]] || { echo FAIL: nix not called >&2; echo "$O" >&2; exit 1; }
            grep -qFx develop /tmp/.vr-b3-nix-argv || { echo FAIL: nix argv missing develop >&2; exit 1; }
            grep -qFx run-task /tmp/.vr-b3-nix-argv || { echo FAIL: nix argv missing run-task >&2; exit 1; }
            grep -qFx test-id /tmp/.vr-b3-nix-argv || { echo FAIL: nix argv missing test-id >&2; exit 1; }
            """);
        var (ec, _, err) = await Run("tty-yes", body);
        if (!string.IsNullOrEmpty(err)) Assert.Fail(err);
        Assert.Equal(0, ec);
    }
    // ═══ Decline tests (n / empty / non-TTY) ═══
    // After declining the offer the bootstrap no longer hard-exits: it prints the
    // manual install hint and falls through to exec the CLI (best-effort
    // devshell), which then runs its own tool gates. So the decline assertions
    // check the hint was shown and the installer was NOT run — not a non-zero
    // exit (the missing-tool hard-fail now lives in the CLI).
    static string NoInstallerStub => "echo ran > /tmp/.vr-b3-installer-ran\nexit 0\n";
    static string DeclineAssert(string extra = "") => $$"""
        O=$(cat /tmp/.vr-b3-out /tmp/.vr-b3-err)
        [[ -f /tmp/.vr-b3-installer-ran ]] && { echo FAIL: installer invoked >&2; exit 1; } || true
        echo "$O" | grep -q 'install.determinate.systems' || { echo FAIL: missing manual hint >&2; echo "$O" >&2; exit 1; }
        {{extra}}
        """;
    [Theory]
    [InlineData("n", true, "")]
    [InlineData("", true, "")]
    [InlineData("n", false, @"echo ""$O"" | grep -q '\[y/N\]' && { echo FAIL: prompt in non-TTY >&2; exit 1; } || true")]
    public async Task Decline_NoInstaller(string stdin, bool tty, string extra)
    {
        var body = tty
            ? TtyTest(stdin, false, false, true, false, false, NoInstallerStub, DeclineAssert(extra))
            : NonTtyTest(false, false, true, false, false, NoInstallerStub, DeclineAssert(extra));
        var (ec, _, err) = await Run($"d-{stdin}-{(tty ? "t" : "n")}", body);
        if (!string.IsNullOrEmpty(err)) Assert.Fail(err);
        Assert.Equal(0, ec);
    }
    // ═══ Nix already present ═══
    [Fact]
    public async Task NixPresent_NoPrompt_ReexecsDirectly()
    {
        var body = TtyTest("y", false, true, true, false, false, NoInstallerStub, assert: """
            O=$(cat /tmp/.vr-b3-out)
            echo "$O" | grep -q '\[y/N\]' && { echo FAIL: prompt with nix present >&2; echo "$O" >&2; exit 1; } || true
            [[ -f /tmp/.vr-b3-installer-ran ]] && { echo FAIL: installer invoked >&2; exit 1; } || true
            [[ -f /tmp/.vr-b3-nix-argv ]] || { echo FAIL: nix not called >&2; echo "$O" >&2; exit 1; }
            grep -qFx develop /tmp/.vr-b3-nix-argv || { echo FAIL: nix argv missing develop >&2; exit 1; }
            """);
        var (ec, _, err) = await Run("nix-pres", body);
        if (!string.IsNullOrEmpty(err)) Assert.Fail(err);
        Assert.Equal(0, ec);
    }
    [Fact]
    public async Task NixPresent_DotnetMissing_NoPrompt_ReexecsDirectly()
    {
        var body = """
            T=$(mktemp -d); S="$T/bin"; trap 'rm -rf "$T" /tmp/.vr-b3-dn-*' EXIT
            rm -f /tmp/.vr-b3-dn-*; mkdir -p "$S" "$T/.relay" "$T/tools/backend"
            cat>"$S/nono"<<'X'&&chmod +x "$S/nono"
            #!/bin/bash
            exit 0
            X
            cat>"$S/nix"<<'X'&&chmod +x "$S/nix"
            #!/bin/bash
            printf '%s\n' "$@" >> /tmp/.vr-b3-dn-nix-argv
            exit 0
            X
            cat>"$S/vr-nix-installer"<<'X'&&chmod +x "$S/vr-nix-installer"
            #!/bin/bash
            echo ran>/tmp/.vr-b3-dn-installer-ran
            exit 0
            X
            cat>"$T/tools/backend/backend.sh"<<'X'&&chmod +x "$T/tools/backend/backend.sh"
            #!/bin/bash
            exit 0
            X
            echo '{"testCmd":"true","bypassSandbox":true}'>"$T/.relay/config.json"
            cp "$LAUNCHER" "$T/visual-relay"; chmod +x "$T/visual-relay"
            cd "$T"
            cat>"$T/run.sh"<<"REOF"
            #!/bin/bash
            export PATH="REPLACE_S:/usr/bin:/bin"
            export VISUAL_RELAY_NIX_INSTALLER="REPLACE_S/vr-nix-installer"
            export VISUAL_RELAY_NIX_REENTRY=
            exec bash "REPLACE_T/visual-relay" launch
            REOF
            sed -e 's|REPLACE_T|'"$T"'|g' -e 's|REPLACE_S|'"$S"'|g' "$T/run.sh">"$T/runf.sh"
            chmod +x "$T/runf.sh"
            _p(){ if [ "$(uname -s)" = Linux ];then script -q -c "$*" /dev/null;else script -q /dev/null "$@";fi;}
            printf 'y\n'|_p "$T/runf.sh">/tmp/.vr-b3-dn-out 2>/tmp/.vr-b3-dn-err; RC=$?
            echo "$RC">/tmp/.vr-b3-dn-rc
            O=$(cat /tmp/.vr-b3-dn-out /tmp/.vr-b3-dn-err)
            echo "$O"|grep -q '\[y/N\]' && { echo FAIL: prompt with nix >&2; echo "$O">&2; exit 1; }||true
            [[ -f /tmp/.vr-b3-dn-installer-ran ]] && { echo FAIL: installer invoked >&2; exit 1; }||true
            [[ -f /tmp/.vr-b3-dn-nix-argv ]]||{ echo FAIL: nix not called >&2; echo "$O">&2; exit 1; }
            grep -qFx develop /tmp/.vr-b3-dn-nix-argv||{ echo FAIL: nix argv missing develop >&2; exit 1; }
            """;
        var (ec, _, err) = await Run("nix-dotnet", body);
        if (!string.IsNullOrEmpty(err)) Assert.Fail(err);
        Assert.Equal(0, ec);
    }
    // ═══ No-nix / no-reentry: reaches install-offer, not silent set -e death ═══
    /// <summary>
    /// When nix is absent and not in a reentry loop, the launcher must reach
    /// the install-offer / tool-missing path — not silently exit 1 via set -e
    /// because _find_nix returned non-zero.  Regression test for the
    /// local nix_bin; nix_bin="$(_find_nix)" pattern at lines 55/67/115/164.
    /// </summary>
    [Fact]
    public async Task NoNix_NoReentry_ReachesToolMissingNotSilentExit()
    {
        var body = NonTtyTest(nono: false, nix: false, dotnet: false,
            bypass: true, reentry: false, isBody: null, assert: """
                RC=$(cat /tmp/.vr-b3-rc); O=$(cat /tmp/.vr-b3-out /tmp/.vr-b3-err)
                (( RC == 127 )) || { echo "FAIL: expected 127 got $RC" >&2; echo "$O" >&2; exit 1; }
                echo "$O" | grep -q 'install.determinate.systems' || { echo "FAIL: missing install hint" >&2; echo "$O" >&2; exit 1; }
                """);
        var (ec, _, err) = await Run("no-nix-no-ree", body);
        if (!string.IsNullOrEmpty(err)) Assert.Fail(err);
        Assert.Equal(0, ec);
    }
    static string ExtractFn(string c, string name)
    {
        var i = c.IndexOf(name + "()", StringComparison.Ordinal);
        if (i < 0) i = c.IndexOf("function " + name, StringComparison.Ordinal);
        Assert.True(i >= 0, $"Function '{name}' not found");
        var b = c.IndexOf('{', i) + 1;
        int p = b, d = 1;
        while (p < c.Length && d > 0)
        {
            if (c[p] == '{') d++; else if (c[p] == '}') { d--; if (d == 0) break; }
            p++;
        }
        Assert.True(d == 0, $"Unmatched braces in '{name}'");
        return c[b..p];
    }
}
