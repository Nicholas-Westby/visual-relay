using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for the <c>visual-relay</c> launcher script changes in installer-5:
/// published-binary preference, sample-reset removal, and SCRIPT_DIR-relative
/// backend.sh invocation. These must FAIL before the implementation lands.
/// </summary>
public sealed class Installer5LauncherTests
{
    private static string RepoRoot => RepoSetup.Root;
    private static string LauncherPath => Path.Combine(RepoRoot, "visual-relay");
    private static string ReadLauncher() => File.ReadAllText(LauncherPath);

    /// <summary>
    /// Runs an embedded bash script that sources the launcher's dispatch logic
    /// in a controlled environment and returns (exitCode, stdout, stderr).
    /// </summary>
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
                await process.StandardOutput.ReadToEndAsync(),
                await process.StandardError.ReadToEndAsync());
        }
        catch (OperationCanceledException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        finally { try { File.Delete(script); } catch { } }
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
            PATH="$STUB_DIR:/usr/bin:/bin" bash "$LAUNCHER" gen-backend-config \
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
        var (exitCode, stdout, stderr) =
            await RunLauncherTestAsync("nix-reentry-args", testBody);
        Assert.Equal(0, exitCode);
        if (!string.IsNullOrEmpty(stderr))
            Assert.Fail($"Regression test failed (args dropped during nix re-entry):\n{stderr}");
    }

    // ── 1. sample-reset removal ──────────────────────────────────────────

    [Fact]
    public void SampleReset_IsNotInUsageMessage()
    {
        var content = ReadLauncher();
        var usageStart = content.IndexOf("echo \"usage:", StringComparison.Ordinal);
        Assert.True(usageStart >= 0, "Launcher must have a usage message");
        var usageLine = content[usageStart..];
        var nl = usageLine.IndexOf('\n');
        if (nl >= 0) usageLine = usageLine[..nl];
        Assert.DoesNotContain("sample-reset", usageLine, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleReset_HasNoDispatchCase()
    {
        var lines = ReadLauncher().Split('\n');
        var inCase = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("case \"$cmd\" in")) { inCase = true; continue; }
            if (inCase && line.TrimStart().StartsWith("esac")) break;
            if (inCase && line.Contains("sample-reset"))
                Assert.Fail($"sample-reset still present in dispatch: '{line.TrimStart()}'");
        }
    }

    // ── 2. Published-binary preference ───────────────────────────────────

    [Fact]
    public void Launcher_ResolvesScriptDirectory()
    {
        var content = ReadLauncher();
        Assert.Contains("SCRIPT_DIR", content, StringComparison.Ordinal);
        Assert.Contains("BASH_SOURCE", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_DefinesPublishedBinaryPaths()
    {
        var content = ReadLauncher();
        Assert.Contains("PUBLISHED_APP", content, StringComparison.Ordinal);
        Assert.Contains("PUBLISHED_INIT", content, StringComparison.Ordinal);
        Assert.Contains("PUBLISHED_GC", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_HasPublishedBinaryExistenceCheck()
    {
        var content = ReadLauncher();
        Assert.Contains("PUBLISHED_APP", content, StringComparison.Ordinal);
        Assert.Contains("-x", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_PrefersPublishedBinaryOverDotnetRun()
    {
        var launchCase = ExtractCaseBody(ReadLauncher(), "launch|run");
        Assert.Contains("PUBLISHED_APP", launchCase, StringComparison.Ordinal);
        Assert.Contains("exec", launchCase, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_PrefersPublishedBinaryOverDotnetRun()
    {
        var initCase = ExtractCaseBody(ReadLauncher(), "init)");
        Assert.Contains("PUBLISHED_INIT", initCase, StringComparison.Ordinal);
        Assert.Contains("exec", initCase, StringComparison.Ordinal);
    }

    [Fact]
    public void GenBackendConfig_PrefersPublishedBinaryOverDotnetRun()
    {
        var gbcCase = ExtractCaseBody(ReadLauncher(), "gen-backend-config)");
        Assert.Contains("PUBLISHED_GC", gbcCase, StringComparison.Ordinal);
        Assert.Contains("exec", gbcCase, StringComparison.Ordinal);
    }

    // ── 3. backend.sh invocation via SCRIPT_DIR ──────────────────────────

    [Fact]
    public void BackendSh_InvokedViaScriptDirRelativePath()
    {
        var launchCase = ExtractCaseBody(ReadLauncher(), "launch|run");
        Assert.Contains("SCRIPT_DIR", launchCase, StringComparison.Ordinal);
        Assert.Contains("backend.sh", launchCase, StringComparison.Ordinal);
    }

    // ── 4. needs_dotnet logic ────────────────────────────────────────────

    [Fact]
    public void NeedsDotnet_LaunchIsConditionalOnPublishedBinary()
    {
        var content = ReadLauncher();
        Assert.Contains("needs_dotnet", content, StringComparison.Ordinal);
        Assert.Contains("HAS_PUBLISHED", content, StringComparison.Ordinal);
    }

    [Fact]
    public void NeedsDotnet_BuildTestCheckAlwaysRequireDotnet()
    {
        var needsDotnetCase = ExtractCaseBody(ReadLauncher(), "needs_dotnet=1");
        Assert.Contains("build", needsDotnetCase, StringComparison.Ordinal);
        Assert.Contains("test", needsDotnetCase, StringComparison.Ordinal);
        Assert.Contains("check", needsDotnetCase, StringComparison.Ordinal);
        Assert.DoesNotContain("sample-reset", needsDotnetCase, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ExtractCaseBody(string content, string caseLabel)
    {
        var startIdx = content.IndexOf(caseLabel, StringComparison.Ordinal);
        Assert.True(startIdx >= 0, $"Case label '{caseLabel}' not found in launcher");
        var bodyStart = content.IndexOf('\n', startIdx) + 1;
        var terminator = content.IndexOf(";;", bodyStart, StringComparison.Ordinal);
        Assert.True(terminator >= 0, $"No ';;' terminator found after '{caseLabel}'");
        return content[bodyStart..terminator];
    }
}
