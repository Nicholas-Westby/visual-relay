using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

// Tool-presence preflight + advisory-noise extractor tests. These exercise the
// pure seams (no live swival/nono): MissingRequiredTools probes an injected PATH,
// ExtractFailureReason drops nono's per-run advisory WARNs, and the runner
// short-circuits before launching a doomed process when a required tool is absent.
public sealed class SwivalSubagentRunnerToolPreflightTests
{
    // ── MissingRequiredTools (PATH-injectable, pure) ──────────────────

    [Fact]
    public void MissingRequiredTools_NeitherToolPresent_SandboxOn_ReturnsBothNames()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix requires nono (Windows requires a sandbox)");
        var config = SandboxOnConfig();
        var emptyPath = string.Empty;

        var missing = SwivalSubagentRunner.MissingRequiredTools(
            config, emptyPath, swivalBinary: "swival", nonoBinary: "nono");

        Assert.Equal(new[] { "swival", "nono" }, missing);
    }

    [Fact]
    public void MissingRequiredTools_BothPresentOnPath_ReturnsEmpty()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix requires nono (Windows requires a sandbox)");
        using var dir = new TempDir();
        var swival = MakeToolFile(dir.Path, "swival");
        var nono = MakeToolFile(dir.Path, "nono");

        var config = SandboxOnConfig();

        var missing = SwivalSubagentRunner.MissingRequiredTools(
            config, dir.Path, swivalBinary: Path.GetFileName(swival), nonoBinary: Path.GetFileName(nono));

        Assert.Empty(missing);
    }

    [Fact]
    public void MissingRequiredTools_OnlySwivalPresent_SandboxOn_ReturnsNono()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Unix requires nono (Windows requires a sandbox)");
        using var dir = new TempDir();
        MakeToolFile(dir.Path, "swival");

        var config = SandboxOnConfig();

        var missing = SwivalSubagentRunner.MissingRequiredTools(
            config, dir.Path, swivalBinary: "swival", nonoBinary: "nono");

        Assert.Equal(new[] { "nono" }, missing);
    }

    // ── Windows: swival via PATHEXT + a sandbox (never nono) ──────────────

    [Fact]
    public void MissingRequiredTools_Windows_SwivalViaPathext_BuiltinSandbox_Runnable()
    {
        using var dir = new TempDir();
        MakeToolFile(dir.Path, "swival.EXE"); // bare "swival" resolves via PATHEXT

        var missing = SwivalSubagentRunner.MissingRequiredTools(
            dir.Path, "swival", "nono", isWindows: true, pathext: ".EXE", windowsMode: WindowsSandboxMode.Builtin);

        Assert.Empty(missing); // swival found, builtin sandbox usable, nono never required
    }

    [Fact]
    public void MissingRequiredTools_Windows_NoSandbox_ReportsSandbox_NeverNono()
    {
        using var dir = new TempDir();
        MakeToolFile(dir.Path, "swival.EXE");

        var missing = SwivalSubagentRunner.MissingRequiredTools(
            dir.Path, "swival", "nono", isWindows: true, pathext: ".EXE", windowsMode: WindowsSandboxMode.Blocked);

        Assert.DoesNotContain("nono", missing);
        Assert.Contains(missing, m => m.Contains("sandbox", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingRequiredTools_Windows_SwivalMissing_Reported()
    {
        using var dir = new TempDir(); // no swival

        var missing = SwivalSubagentRunner.MissingRequiredTools(
            dir.Path, "swival", "nono", isWindows: true, pathext: ".EXE", windowsMode: WindowsSandboxMode.Builtin);

        Assert.Contains("swival", missing);
    }

    // ── Preflight short-circuit in RunAsync ───────────────────────────

    [Fact]
    public async Task RunAsync_RequiredToolMissing_ShortCircuitsWithLegibleError()
    {
        using var repo = TestRepository.Create();
        // A swival binary name guaranteed not to resolve against any PATH dir.
        var absent = "swival-" + Guid.NewGuid().ToString("N");
        var runner = new SwivalSubagentRunner(
            SandboxOnConfig(), absent, backendProbe: SwivalTestHelpers.AlwaysReady);

        var result = await runner.RunAsync(SwivalTestHelpers.Invocation(repo.Root));

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        // Names the real cause and points at installing swival, NOT a sandbox rule.
        Assert.Contains("swival", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PATH", result.Error, StringComparison.OrdinalIgnoreCase);
        // None of nono's advisory red herrings.
        Assert.DoesNotContain("deny_shell_configs", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("bypass-protection", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(".envrc", result.Error, StringComparison.Ordinal);
    }

    // ── ExtractFailureReason: drop advisory WARNs, surface the real line ──

    [Fact]
    public void ExtractFailureReason_RealArtifactShape_SurfacesBinaryPathNotEnvrcNoise()
    {
        // Output shaped like the ground-truth capture: dozens of advisory WARNs
        // (incl. the .envrc / deny_shell_configs one), a Capabilities banner, then
        // the real fatal line at the very end.
        var output = string.Join('\n', new[]
        {
            "WARN '/Users/nicholaswestby/.ssh' is blocked by 'deny_credentials'; use --bypass-protection /Users/nicholaswestby/.ssh to allow access",
            "WARN '/Users/nicholaswestby/Library/Keychains' is blocked by 'deny_keychains_macos'; use --bypass-protection /Users/nicholaswestby/Library/Keychains to allow access",
            "WARN '/Users/nicholaswestby/.bash_history' is blocked by 'deny_shell_history'; use --bypass-protection /Users/nicholaswestby/.bash_history to allow access",
            "WARN '/Users/nicholaswestby/.envrc' is blocked by 'deny_shell_configs'; use --bypass-protection /Users/nicholaswestby/.envrc to allow access",
            "  ────────────────────────────────────────────────────",
            "   net  outbound allowed",
            "  ────────────────────────────────────────────────────",
            "",
            "nono: Command execution failed: swival: cannot find binary path",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.Contains("cannot find binary path", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("deny_shell_configs", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("bypass-protection", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(".envrc", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFailureReason_NoFailureSignal_FallsBackToLastNonEmptyLine()
    {
        // No line looks like a failure, so the extractor keeps the surviving tail
        // after dropping the advisory WARN — i.e. the final non-empty real line.
        var output = string.Join('\n', new[]
        {
            "WARN '/Users/me/.ssh' is blocked by 'deny_credentials'; use --bypass-protection /Users/me/.ssh to allow access",
            "swival completed with some final summary",
            "",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.Equal("swival completed with some final summary", reason);
        Assert.DoesNotContain("bypass-protection", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFailureReason_BenignErrorCountLineBeforeFatal_AnchorsOnTheFatalLine()
    {
        // A benign pre-failure line that merely *mentions* "errors" must not be
        // mis-selected as the failure anchor: the strong-signal pass must win and
        // anchor on the real fatal line.
        var output = string.Join('\n', new[]
        {
            "INFO loaded config with 0 errors",
            "INFO starting subagent",
            "nono: Command execution failed: swival: cannot find binary path",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.Contains("cannot find binary path", reason, StringComparison.Ordinal);
        // The surfaced reason must NOT lead with / include the benign info line.
        Assert.DoesNotContain("loaded config with 0 errors", reason, StringComparison.Ordinal);
        Assert.StartsWith("nono: Command execution failed", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFailureReason_AllAdvisoryNoise_FallsBackToNoDiagnosticPlaceholder()
    {
        // Every line is either an advisory WARN or pure decoration, so nothing
        // survives filtering. The extractor must surface a sensible placeholder so
        // the caller never emits a dangling "swival exit 1: " with no cause.
        var output = string.Join('\n', new[]
        {
            "WARN '/Users/me/.ssh' is blocked by 'deny_credentials'; use --bypass-protection /Users/me/.ssh to allow access",
            "WARN '/Users/me/.envrc' is blocked by 'deny_shell_configs'; use --bypass-protection /Users/me/.envrc to allow access",
            "  ────────────────────────────────────────────────────",
            "",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.Equal("(no diagnostic output captured)", reason);
        Assert.DoesNotContain("bypass-protection", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFailureReason_EmptyOutput_FallsBackToNoDiagnosticPlaceholder()
    {
        Assert.Equal("(no diagnostic output captured)", SwivalSubagentRunner.ExtractFailureReason(string.Empty));
        Assert.Equal("(no diagnostic output captured)", SwivalSubagentRunner.ExtractFailureReason("   \n\n  "));
    }

    [Fact]
    public void ExtractFailureReason_FailureLineWithTraceback_KeepsMultiLineTail()
    {
        // A genuine multi-line failure block (anchor line + following frames) must
        // survive intact from the anchor down to the end.
        var output = string.Join('\n', new[]
        {
            "WARN '/Users/me/.ssh' is blocked by 'deny_credentials'; use --bypass-protection /Users/me/.ssh to allow access",
            "Traceback (most recent call last):",
            "  File \"runner.py\", line 10, in <module>",
            "    raise RuntimeError(\"boom\")",
            "RuntimeError: boom",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.StartsWith("Traceback (most recent call last):", reason, StringComparison.Ordinal);
        Assert.Contains("RuntimeError: boom", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("bypass-protection", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractFailureReason_PrefersFailureLineOverTrailingDecoration()
    {
        var output = string.Join('\n', new[]
        {
            "nono: Command execution failed: swival: cannot find binary path",
            "  ────────────────────────────────────────────────────",
            "",
        });

        var reason = SwivalSubagentRunner.ExtractFailureReason(output);

        Assert.Contains("cannot find binary path", reason, StringComparison.Ordinal);
    }

    private static string MakeToolFile(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/usr/bin/env bash\n");
        return path;
    }

    private static RelayConfig SandboxOnConfig() =>
        new(
            "llm-tasks",
            "true",
            "true",
            [],
            new Dictionary<string, string> { ["cheap"] = "cheap" },
            true,
            1,
            1,
            false,
            true,
            0,
            300_000,
            new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
            660_000,
            2,
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "vr-tool-preflight-" + Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
