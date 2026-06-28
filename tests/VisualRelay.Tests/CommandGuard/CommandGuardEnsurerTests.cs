using VisualRelay.Core.Execution;

namespace VisualRelay.Tests.CommandGuard;

/// <summary>
/// Tests for <see cref="CommandGuardEnsurer"/> — ensures the guard binary is
/// published to the expected path and the incremental no-op fast-path works.
///
/// <para>These tests exercise the public <see cref="CommandGuardEnsurer.EnsureAsync"/>
/// entry point with temporary directories. The actual <c>dotnet publish</c>
/// invocation is covered by the live <c>run-task</c> smoke test, not by these
/// fast unit tests.</para>
///
/// <para>ALL tests MUST FAIL against the current tree: there are zero
/// CommandGuardEnsurer tests today (defect 4).</para>
/// </summary>
public sealed class CommandGuardEnsurerTests
{
    // ── No-source-dir path ──────────────────────────────────────────

    [Fact]
    public async Task EnsureAsync_NoSourceDir_NoBinary_ReturnsNull()
    {
        using var tmp = new TempDir();
        // No tools/VisualRelay.CommandGuard directory, no command-guard/ binary.

        var result = await CommandGuardEnsurer.EnsureAsync(tmp.Root);

        Assert.Null(result);
    }

    [Fact]
    public async Task EnsureAsync_NoSourceDir_ExistingBinary_ReturnsPath()
    {
        using var tmp = new TempDir();
        var binaryPath = Path.Combine(tmp.Root, "command-guard", "VisualRelay.CommandGuard");
        Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
        File.WriteAllText(binaryPath, "dummy");

        var result = await CommandGuardEnsurer.EnsureAsync(tmp.Root);

        Assert.Equal(binaryPath, result);
        Assert.True(File.Exists(result), "binary must exist at returned path");
    }

    // ── Incremental no-op path (IsUpToDate fast-path) ───────────────

    [Fact]
    public async Task EnsureAsync_SourceDir_UpToDateBinary_ReturnsPathWithoutRepublish()
    {
        using var tmp = new TempDir();

        // Create an empty project source directory.
        var projectDir = Path.Combine(tmp.Root, "tools", "VisualRelay.CommandGuard");
        Directory.CreateDirectory(projectDir);

        // Create a dummy binary that is newer than the source dir files
        // (an empty source dir has no files, so the binary is trivially
        // up to date).
        var binaryPath = Path.Combine(tmp.Root, "command-guard", "VisualRelay.CommandGuard");
        Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
        File.WriteAllText(binaryPath, "dummy");
        // Touch the binary to ensure it has a recent timestamp.
        File.SetLastWriteTimeUtc(binaryPath, DateTime.UtcNow);

        var result = await CommandGuardEnsurer.EnsureAsync(tmp.Root);

        Assert.Equal(binaryPath, result);
        Assert.True(File.Exists(result), "binary must exist at returned path");
    }

    [Fact]
    public async Task EnsureAsync_SourceDir_StaleBinary_DetectsOutOfDate()
    {
        SkipIfNotOptedIn();

        using var tmp = new TempDir();

        // Create a project source directory with a source file newer
        // than the binary — the IsUpToDate check must return false,
        // which triggers dotnet publish (which will fail because the
        // csproj is a dummy, causing fallback).
        var projectDir = Path.Combine(tmp.Root, "tools", "VisualRelay.CommandGuard");
        Directory.CreateDirectory(projectDir);
        var sourceFile = Path.Combine(projectDir, "SomeSource.cs");
        File.WriteAllText(sourceFile, "// dummy source");

        var binaryPath = Path.Combine(tmp.Root, "command-guard", "VisualRelay.CommandGuard");
        Directory.CreateDirectory(Path.GetDirectoryName(binaryPath)!);
        File.WriteAllText(binaryPath, "dummy");

        // Make source newer than binary.
        File.SetLastWriteTimeUtc(binaryPath, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(sourceFile, DateTime.UtcNow);

        var result = await CommandGuardEnsurer.EnsureAsync(tmp.Root);

        // The publish will fail (no real project), so we fall back to the
        // existing binary path.  The key assertion: the method completes
        // without throwing and returns the existing binary path.
        Assert.Equal(binaryPath, result);
        Assert.True(File.Exists(result), "existing binary must survive fallback");
    }

    [Fact]
    public async Task EnsureAsync_ResolvesBinaryPathUnderCommandGuardDir()
    {
        using var tmp = new TempDir();
        var expected = Path.Combine(tmp.Root, "command-guard", "VisualRelay.CommandGuard");
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllText(expected, "dummy");

        var result = await CommandGuardEnsurer.EnsureAsync(tmp.Root);

        Assert.Equal(expected, result);
    }

    // ── Opt-in gate ─────────────────────────────────────────────────

    // The one test here that triggers a REAL `dotnet publish`. Under the
    // verify's nono sandbox the child dotnet can wedge (it intermittently
    // blocks on the denied com.apple.SecurityServer mach-lookup), tripping the
    // blame-hang collector and aborting the whole run. Opt-in only so the
    // default sandboxed verify stays wedge-proof; the product still bounds the
    // publish with a 45 s fail-open timeout, and the no-publish fast paths keep
    // their default coverage.
    private static void SkipIfNotOptedIn() =>
        NonoIntegration.SkipIfNotOptedIn("VR_RUN_NONO_INTEGRATION=1 required: runs a real dotnet publish.");

    // ── TempDir helper ──────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public string Root { get; }

        public TempDir()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "vr-cg-ensurer-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch
            {
                // Swallow — leaking a temp dir is acceptable.
            }
        }
    }
}
