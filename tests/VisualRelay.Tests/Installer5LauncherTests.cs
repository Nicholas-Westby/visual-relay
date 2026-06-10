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

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string ReadLauncher() =>
        File.ReadAllText(LauncherPath);

    private static string[] ReadLauncherLines() =>
        File.ReadAllLines(LauncherPath);

    /// <summary>
    /// Runs a short embedded bash script that sources the launcher's
    /// dispatch logic in a controlled environment and exits with a code
    /// + stdout that we assert on.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunLauncherTestAsync(
        string testName,
        string testBody)
    {
        var script = Path.Combine(Path.GetTempPath(), $"vr-launcher-test-{testName}.sh");
        var escapedLauncherPath = LauncherPath.Replace("'", "'\\''");

        var fullScript = $$"""
            #!/usr/bin/env bash
            set -euo pipefail

            # Source the launcher but override the dispatch to avoid actually
            # running dotnet or launching the app. We capture what WOULD happen.
            LAUNCHER='{{escapedLauncherPath}}'

            {{testBody}}
            """;

        await File.WriteAllTextAsync(script, fullScript);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

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
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
            throw;
        }
        finally
        {
            try { File.Delete(script); } catch { /* best-effort */ }
        }
    }

    // ── 0. nix re-entry argument preservation ────────────────────────────

    /// <summary>
    /// When dotnet is absent from PATH and nix IS present, the launcher
    /// re-enters itself through <c>nix develop</c>.  That re-entry must
    /// preserve every subcommand argument byte-for-byte, including
    /// arguments that contain spaces.
    /// </summary>
    [Fact]
    public async Task NixReentry_PreservesSubcommandArguments()
    {
        var testBody = """
            NIX_LOG="/tmp/.vr-test-nix-argv"
            STUB_DIR="/tmp/.vr-test-stub-bin"
            FAKE_TMPL="/tmp/.vr-test-fake-template.yaml"

            rm -rf "$NIX_LOG" "$STUB_DIR" "$FAKE_TMPL"
            mkdir -p "$STUB_DIR"

            # Create a stub nix executable that logs every argument (one per line)
            cat > "$STUB_DIR/nix" << 'NIXEOF'
            #!/bin/bash
            printf '%s\n' "$@" >> /tmp/.vr-test-nix-argv
            exit 0
            NIXEOF
            chmod +x "$STUB_DIR/nix"

            echo "fake" > "$FAKE_TMPL"

            # PATH excludes dotnet, so _require_dotnet() reaches the nix re-entry.
            # The trailing '|| true' keeps set -e from killing the test when dotnet
            # happens to be on PATH (launcher runs dotnet run, which fails fast).
            PATH="$STUB_DIR:/usr/bin:/bin" bash "$LAUNCHER" gen-backend-config "$FAKE_TMPL" 'arg with spaces' 2>/dev/null || true

            # Verify every subcommand argument survived the nix re-entry.
            if ! grep -qFx 'gen-backend-config' "$NIX_LOG"; then
                echo "FAIL: 'gen-backend-config' not in nix argv log" >&2
                echo "=== nix argv log ===" >&2
                cat "$NIX_LOG" >&2
                rm -rf "$NIX_LOG" "$STUB_DIR" "$FAKE_TMPL"
                exit 1
            fi

            if ! grep -qFx '/tmp/.vr-test-fake-template.yaml' "$NIX_LOG"; then
                echo "FAIL: template path not in nix argv log" >&2
                echo "=== nix argv log ===" >&2
                cat "$NIX_LOG" >&2
                rm -rf "$NIX_LOG" "$STUB_DIR" "$FAKE_TMPL"
                exit 1
            fi

            if ! grep -qFx 'arg with spaces' "$NIX_LOG"; then
                echo "FAIL: spaced argument not in nix argv log" >&2
                echo "=== nix argv log ===" >&2
                cat "$NIX_LOG" >&2
                rm -rf "$NIX_LOG" "$STUB_DIR" "$FAKE_TMPL"
                exit 1
            fi

            rm -rf "$NIX_LOG" "$STUB_DIR" "$FAKE_TMPL"
            """;

        var (exitCode, stdout, stderr) =
            await RunLauncherTestAsync("nix-reentry-args", testBody);

        Assert.Equal(0, exitCode);

        // On failure the bash script writes diagnostics to stderr.
        if (!string.IsNullOrEmpty(stderr))
        {
            Assert.Fail($"Regression test failed (args dropped during nix re-entry):\n{stderr}");
        }
    }

    // ── 1. sample-reset removal ──────────────────────────────────────────

    [Fact]
    public void SampleReset_IsNotInUsageMessage()
    {
        var content = ReadLauncher();

        // The usage/help message (the *) case) must NOT mention sample-reset.
        // Find the usage line — it starts with "echo \"usage:".
        var usageStart = content.IndexOf("echo \"usage:", StringComparison.Ordinal);
        Assert.True(usageStart >= 0, "Launcher must have a usage message");

        // Extract from usage line to end of string (or next newline in the echo).
        var usageSection = content[usageStart..];
        var usageEnd = usageSection.IndexOf('\n');
        var usageLine = usageEnd >= 0 ? usageSection[..usageEnd] : usageSection;

        Assert.DoesNotContain("sample-reset", usageLine, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleReset_HasNoDispatchCase()
    {
        var lines = ReadLauncherLines();
        var inCase = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("case \"$cmd\" in"))
            {
                inCase = true;
                continue;
            }

            if (inCase && line.TrimStart().StartsWith("esac"))
            {
                break;
            }

            if (inCase)
            {
                // Any case pattern containing sample-reset is a failure.
                var trimmed = line.TrimStart();
                if (trimmed.Contains("sample-reset"))
                {
                    Assert.Fail($"sample-reset still present in dispatch: '{trimmed}'");
                }
            }
        }
    }

    // ── 2. Published-binary preference ───────────────────────────────────

    [Fact]
    public void Launcher_ResolvesScriptDirectory()
    {
        var content = ReadLauncher();

        // The launcher must compute its own directory (SCRIPT_DIR) using
        // BASH_SOURCE / readlink resolution so published binaries are found
        // relative to the script, not CWD.
        Assert.Contains("SCRIPT_DIR", content, StringComparison.Ordinal);
        Assert.Contains("BASH_SOURCE", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_DefinesPublishedBinaryPaths()
    {
        var content = ReadLauncher();

        // The launcher must define paths for published App, Init, and
        // GenBackendConfig binaries.
        Assert.Contains("PUBLISHED_APP", content, StringComparison.Ordinal);
        Assert.Contains("PUBLISHED_INIT", content, StringComparison.Ordinal);
        Assert.Contains("PUBLISHED_GC", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Launcher_HasPublishedBinaryExistenceCheck()
    {
        var content = ReadLauncher();

        // Must check whether the published app binary is executable.
        // Typical pattern: [[ -x "$PUBLISHED_APP" ]]
        Assert.Contains("PUBLISHED_APP", content, StringComparison.Ordinal);
        Assert.Contains("-x", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Launch_PrefersPublishedBinaryOverDotnetRun()
    {
        var content = ReadLauncher();

        // The launch|run dispatch case must contain a conditional that
        // prefers the published binary (exec) over dotnet run.
        var launchCase = ExtractCaseBody(content, "launch|run");

        Assert.Contains("PUBLISHED_APP", launchCase, StringComparison.Ordinal);
        // Must use exec for the published binary path (not dotnet run).
        Assert.Contains("exec", launchCase, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_PrefersPublishedBinaryOverDotnetRun()
    {
        var content = ReadLauncher();

        var initCase = ExtractCaseBody(content, "init)");

        // init must also prefer published binary when available.
        Assert.Contains("PUBLISHED_INIT", initCase, StringComparison.Ordinal);
        Assert.Contains("exec", initCase, StringComparison.Ordinal);
    }

    [Fact]
    public void GenBackendConfig_PrefersPublishedBinaryOverDotnetRun()
    {
        var content = ReadLauncher();

        // Find the gen-backend-config case body.
        var gbcCase = ExtractCaseBody(content, "gen-backend-config)");

        Assert.Contains("PUBLISHED_GC", gbcCase, StringComparison.Ordinal);
        Assert.Contains("exec", gbcCase, StringComparison.Ordinal);
    }

    // ── 3. backend.sh invocation via SCRIPT_DIR ──────────────────────────

    [Fact]
    public void BackendSh_InvokedViaScriptDirRelativePath()
    {
        var content = ReadLauncher();

        // The backend.sh invocation must use SCRIPT_DIR, not a bare
        // relative path like tools/backend/backend.sh, so it works
        // regardless of CWD (important for brew install).
        var launchCase = ExtractCaseBody(content, "launch|run");

        // Must reference SCRIPT_DIR when calling backend.sh.
        Assert.Contains("SCRIPT_DIR", launchCase, StringComparison.Ordinal);
        Assert.Contains("backend.sh", launchCase, StringComparison.Ordinal);
    }

    // ── 4. needs_dotnet logic ────────────────────────────────────────────

    [Fact]
    public void NeedsDotnet_LaunchIsConditionalOnPublishedBinary()
    {
        var content = ReadLauncher();

        // The needs_dotnet case for launch|run must be conditional:
        // if published binary exists, needs_dotnet=0; else 1.
        // We can't test runtime behavior without a real binary, but we
        // can verify the script contains the conditional logic.
        Assert.Contains("needs_dotnet", content, StringComparison.Ordinal);

        // The launch case should reference HAS_PUBLISHED or similar
        // flag for the conditional.
        Assert.Contains("HAS_PUBLISHED", content, StringComparison.Ordinal);
    }

    [Fact]
    public void NeedsDotnet_BuildTestCheckAlwaysRequireDotnet()
    {
        var content = ReadLauncher();

        // build|test|format|screenshot|run-task|check must still always
        // require dotnet (they have no published equivalent).
        var needsDotnetCase = ExtractCaseBody(content, "needs_dotnet=1");

        Assert.Contains("build", needsDotnetCase, StringComparison.Ordinal);
        Assert.Contains("test", needsDotnetCase, StringComparison.Ordinal);
        Assert.Contains("check", needsDotnetCase, StringComparison.Ordinal);
        Assert.DoesNotContain("sample-reset", needsDotnetCase, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the body of a case branch from the launcher script.
    /// Looks for the pattern starting with the case label and ending at ;;.
    /// </summary>
    private static string ExtractCaseBody(string content, string caseLabel)
    {
        var startIdx = content.IndexOf(caseLabel, StringComparison.Ordinal);
        Assert.True(startIdx >= 0, $"Case label '{caseLabel}' not found in launcher");

        // Find the ;; terminator after the case label.
        var bodyStart = content.IndexOf('\n', startIdx) + 1;
        var terminator = content.IndexOf(";;", bodyStart, StringComparison.Ordinal);
        Assert.True(terminator >= 0, $"No ';;' terminator found after '{caseLabel}'");

        return content[bodyStart..terminator];
    }
}
