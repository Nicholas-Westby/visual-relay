using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the <c>visual-relay</c> launcher script changes in installer-5:
/// published-binary preference, sample-reset removal, and SCRIPT_DIR-relative
/// backend.sh invocation. These must FAIL before the implementation lands.
/// </summary>
public sealed partial class Installer5LauncherTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string LauncherPath => Path.Combine(RepoRoot, "visual-relay");
    private static string BackendShPath => Path.Combine(RepoRoot, "tools", "backend", "backend.sh");
    private static string ReadLauncher() => File.ReadAllText(LauncherPath);
    private static string ReadBackendSh() => File.ReadAllText(BackendShPath);

    /// <summary>Runs an embedded bash script that sources the launcher's dispatch
    /// logic in a controlled environment and returns (exitCode, stdout, stderr).</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunLauncherTestAsync(
        string testName, string testBody)
    {
        var script = Path.Combine(Path.GetTempPath(), $"vr-launcher-test-{testName}.sh");
        var escapedLauncherPath = LauncherPath.Replace("'", "'\\''");
        var fullScript = $$"""
            #!/usr/bin/env bash
            set -euo pipefail
            LAUNCHER='{{escapedLauncherPath}}'
            {{testBody}}
            """;
        await File.WriteAllTextAsync(script, fullScript);
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

    // ── 0. nix re-entry argument preservation ────────────────────────────

    [Fact]
    public async Task NixReentry_PreservesSubcommandArguments()
    {
        var testBody = """
            NIX_LOG="/tmp/.vr-test-nix-argv"; STUB_DIR="/tmp/.vr-test-stub-bin"
            FAKE_TMPL="/tmp/.vr-test-fake-template.yaml"
            rm -rf "$NIX_LOG" "$STUB_DIR" "$FAKE_TMPL"; mkdir -p "$STUB_DIR"
            cat > "$STUB_DIR/nix" << 'X' && chmod +x "$STUB_DIR/nix"
            #!/bin/bash
            printf '%s\n' "$@" >> /tmp/.vr-test-nix-argv
            exit 0
            X
            echo "fake" > "$FAKE_TMPL"
            PATH="$STUB_DIR:/usr/bin:/bin" VISUAL_RELAY_NIX_REENTRY= bash "$LAUNCHER" gen-backend-config \
                "$FAKE_TMPL" 'arg with spaces' 2>/dev/null || true
            for arg in 'gen-backend-config' '/tmp/.vr-test-fake-template.yaml' 'arg with spaces'; do
                if ! grep -qFx "$arg" "$NIX_LOG"; then
                    echo "FAIL: '$arg' not in nix argv log" >&2
                    cat "$NIX_LOG" >&2
                    rm -rf "$NIX_LOG" "$STUB_DIR" "$FAKE_TMPL"; exit 1
                fi
            done
            rm -rf "$NIX_LOG" "$STUB_DIR" "$FAKE_TMPL"
            """;
        var (exitCode, _, stderr) =
            await RunLauncherTestAsync("nix-reentry-args", testBody);
        Assert.Equal(0, exitCode);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Regression test failed (args dropped during nix re-entry):\n{stderr}");
    }

    // ── 1. sample-reset removal ──────────────────────────────────────────

    // The bootstrap no longer holds a usage message or a command `case` (both
    // moved to VisualRelay.Cli); sample-reset's absence from the user-visible
    // command set is asserted by CliCommandRouterTests. The bootstrap must simply
    // never mention sample-reset.
    [Fact]
    public void SampleReset_NotMentionedInBootstrap()
    {
        Assert.DoesNotContain("sample-reset", ReadLauncher(), StringComparison.Ordinal);
    }

    // ── 2. Published-binary preference ───────────────────────────────────

    [Fact]
    public void Launcher_ResolvesScriptDirectory()
    {
        var content = ReadLauncher();
        Assert.Contains("SCRIPT_DIR", content, StringComparison.Ordinal);
        Assert.Contains("BASH_SOURCE", content, StringComparison.Ordinal);
    }

    // The bootstrap defines only the published APP path (the brew `launch` fast
    // path it must own before dotnet exists); the published init/gen-backend-config
    // fast paths moved into the CLI (CliInitCommandTests covers init's). The
    // per-command published-init preference is covered by CliInitCommandTests.
    [Fact]
    public void Launcher_DefinesPublishedAppPath()
    {
        Assert.Contains("PUBLISHED_APP", ReadLauncher(), StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_HasPublishedBinaryExistenceCheck()
    {
        var content = ReadLauncher();
        Assert.Contains("PUBLISHED_APP", content, StringComparison.Ordinal);
        Assert.Contains("-x", content, StringComparison.Ordinal);
    }

    /// <summary>The brew fast path stays in bash: when a published app exists and
    /// the verb is launch/run, the bootstrap execs it directly (no dotnet). It is
    /// now an `if` guard rather than a `case` branch.</summary>
    [Fact]
    public void Launch_PrefersPublishedBinaryOverDotnetRun()
    {
        var content = ReadLauncher();
        Assert.Contains("HAS_PUBLISHED", content, StringComparison.Ordinal);
        Assert.Contains("PUBLISHED_APP", content, StringComparison.Ordinal);
        Assert.Matches(@"\$cmd""?\s*==\s*launch", content);
        Assert.Contains("exec \"$PUBLISHED_APP", content, StringComparison.Ordinal);
    }

    // ── 5. Self-edit parse safety ───────────────────────────────────────

    /// <summary>Launcher's last non-blank line must be <c>main "$@"; exit $?</c>
    /// so bash parses all control flow before any subcommand executes.</summary>
    [Fact]
    public void Launcher_EndsWithMainInvocation()
    {
        var lines = ReadLauncher().Split('\n');
        var lastNonBlank = lines
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0);
        Assert.NotNull(lastNonBlank);
        Assert.Matches(@"^main\s+""\$@""\s*;\s*exit\s+\$\?$", lastNonBlank!);
    }

    /// <summary>Same structural guard for <c>tools/backend/backend.sh</c>.</summary>
    [Fact]
    public void BackendSh_EndsWithMainInvocation()
    {
        var lines = ReadBackendSh().Split('\n');
        var lastNonBlank = lines
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0);
        Assert.NotNull(lastNonBlank);
        Assert.Matches(@"^main\s+""\$@""\s*;\s*exit\s+\$\?$", lastNonBlank!);
    }

    /// <summary>Stubs dotnet to append garbage to the running launcher, then
    /// asserts clean exit. Before the function-wrap fix bash would resume parsing
    /// after the edit and hit a syntax error; after the fix all control flow is
    /// parsed before any subcommand executes.</summary>
    [Fact]
    public async Task SelfEdit_StubDotnetAppendsGarbage_LauncherStillExitsZero()
    {
        var testBody = """
            TEST_DIR=$(mktemp -d); STUB_DIR="$TEST_DIR/bin"
            LAUNCHER_COPY="$TEST_DIR/visual-relay"
            trap 'rm -rf "$TEST_DIR" /tmp/.vr-selfedit-*' EXIT

            # Copy the real launcher to our temp dir
            cp "$LAUNCHER" "$LAUNCHER_COPY"
            chmod +x "$LAUNCHER_COPY"

            # Stub dotnet: appends garbage to the running script, then exits 0
            mkdir -p "$STUB_DIR"
            cat > "$STUB_DIR/dotnet" << 'X' && chmod +x "$STUB_DIR/dotnet"
            #!/bin/bash
            echo 'garbage )(' >> "$SELFEDIT_TARGET"
            exit 0
            X

            # Stub swival: hard-required tool, must be present for run-task to proceed
            cat > "$STUB_DIR/swival" << 'X' && chmod +x "$STUB_DIR/swival"
            #!/bin/bash
            exit 0
            X

            # Create .relay/config.json with bypassSandbox:true to skip nono
            mkdir -p "$TEST_DIR/.relay"
            echo '{"bypassSandbox":true}' > "$TEST_DIR/.relay/config.json"

            cd "$TEST_DIR"
            RC=0
            SELFEDIT_TARGET="$LAUNCHER_COPY" VISUAL_RELAY_NIX_REENTRY=1 PATH="$STUB_DIR:/usr/bin:/bin" \
                bash "$LAUNCHER_COPY" run-task test-id \
                >/tmp/.vr-selfedit-out 2>/tmp/.vr-selfedit-err || RC=$?

            if (( RC != 0 )); then
                echo "FAIL: launcher exited $RC, expected 0 (self-edit parse hazard?)" >&2
                echo "=== stdout ===" >&2; cat /tmp/.vr-selfedit-out >&2
                echo "=== stderr ===" >&2; cat /tmp/.vr-selfedit-err >&2
                exit 1
            fi

            if grep -qi "syntax error" /tmp/.vr-selfedit-err; then
                echo "FAIL: syntax error on stderr (self-edit parse hazard hit)" >&2
                echo "=== stderr ===" >&2; cat /tmp/.vr-selfedit-err >&2
                exit 1
            fi
            """;

        var (exitCode, _, stderr) = await RunLauncherTestAsync("selfedit-garbage", testBody);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Test failed:\n{stderr}");
        Assert.Equal(0, exitCode);
    }
}
