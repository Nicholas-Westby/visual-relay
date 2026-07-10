using System.Text;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <see cref="UserEnvSnapshot"/> loading and
/// <see cref="SwivalSubagentRunner.BuildTargetCommandEnvironment"/> merge
/// semantics. These reference types that WILL exist after implementation;
/// until then they FAIL TO COMPILE — the required red-first gate.
/// </summary>
public sealed class UserEnvSnapshotTests : IDisposable
{
    private readonly DictionaryEnvironmentAccessor _env = new();
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best-effort temp cleanup */ }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    // UserEnvSnapshot.Load
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void Load_WithValidSnapshot_ReturnsDict()
    {
        var snapshotPath = WriteSnapshot(
            ("VR_SNAP_MARKER", "hello"),
            ("PATH", "/usr/bin:/bin"),
            ("HOME", "/home/testuser"));

        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshotPath;

        var result = UserEnvSnapshot.Load(_env);

        Assert.NotNull(result);
        Assert.Equal("hello", result["VR_SNAP_MARKER"]);
        Assert.Equal("/usr/bin:/bin", result["PATH"]);
        Assert.Equal("/home/testuser", result["HOME"]);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Load_WhenEnvVarNotSet_ReturnsNull()
    {
        // Deliberately do NOT set VISUAL_RELAY_USER_ENV_SNAPSHOT.
        var result = UserEnvSnapshot.Load(_env);

        Assert.Null(result);
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsNull()
    {
        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = "/nonexistent/path/user-env";

        var result = UserEnvSnapshot.Load(_env);

        Assert.Null(result);
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyDict()
    {
        var snapshotPath = WriteSnapshot(); // no entries

        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshotPath;

        var result = UserEnvSnapshot.Load(_env);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ════════════════════════════════════════════════════════════════════
    // BuildTargetCommandEnvironment
    // ════════════════════════════════════════════════════════════════════

    [Fact]
    public void BuildTargetCommandEnvironment_WithSnapshot_MergesUserBaseWithVROverrides()
    {
        // Snapshot has a marker var AND a var that VR overrides.
        var snapshotPath = WriteSnapshot(
            ("VR_SNAP_MARKER", "present"),
            ("PYTHONDONTWRITEBYTECODE", "0"),   // will be overridden by VR
            ("SOME_USER_VAR", "user-value"));

        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshotPath;

        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env);

        Assert.NotNull(result);
        // Marker from user env survives.
        Assert.Equal("present", result["VR_SNAP_MARKER"]);
        // User var survives.
        Assert.Equal("user-value", result["SOME_USER_VAR"]);
        // VR override wins on collision.
        Assert.Equal("1", result["PYTHONDONTWRITEBYTECODE"]);
        // VR-specific overrides are present.
        Assert.Equal("1", result["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("1", result["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        // Snapshot does NOT have SDKROOT (user's real env doesn't have nix vars).
        Assert.False(result.ContainsKey("SDKROOT"));
    }

    [Fact]
    public void BuildTargetCommandEnvironment_WithoutSnapshot_FallsBackToSandboxOverridesOnly()
    {
        // No VISUAL_RELAY_USER_ENV_SNAPSHOT set — packaged/brew path.

        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env);

        Assert.NotNull(result);
        // Same shape as BuildSandboxEnvironment — VR override keys present.
        Assert.Equal("1", result["PYTHONDONTWRITEBYTECODE"]);
        Assert.Equal("1", result["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("1", result["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        // No snapshot keys bleed in.
        Assert.False(result.ContainsKey("VR_SNAP_MARKER"));
    }

    [Fact]
    public void BuildTargetCommandEnvironment_SnapshotDoesNotReintroduceNixOnlyVars()
    {
        // The snapshot captures the USER'S pre-devshell env — it should NOT have
        // nix-injected vars like SDKROOT or DEVELOPER_DIR. This test proves the
        // snapshot path doesn't accidentally add them back. (The actual stripping
        // of nix vars that SURVIVE the devshell is handled by
        // ProcessCapture.StripLeakedNixSdkEnv; this test guards the snapshot
        // source itself.)
        var snapshotPath = WriteSnapshot(
            ("HOME", "/home/user"),
            ("PATH", "/usr/local/bin:/usr/bin:/bin"),
            ("TMPDIR", "/var/folders/ab/cdefg/T"));

        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshotPath;

        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env);

        Assert.NotNull(result);
        // These are nix-only vars — the user's pre-devshell env shouldn't have them.
        Assert.False(result.ContainsKey("SDKROOT"));
        Assert.False(result.ContainsKey("DEVELOPER_DIR"));
        // User vars pass through.
        Assert.Equal("/home/user", result["HOME"]);
        Assert.Equal("/var/folders/ab/cdefg/T", result["TMPDIR"]);
    }

    // ════════════════════════════════════════════════════════════════════
    // Helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Writes a NUL-delimited env file (env -0 format) and returns its path.</summary>
    private string WriteSnapshot(params (string Key, string Value)[] entries)
    {
        var path = Path.GetTempFileName();
        _tempFiles.Add(path);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        foreach (var (key, value) in entries)
        {
            stream.Write(Encoding.UTF8.GetBytes($"{key}={value}"));
            stream.WriteByte(0);
        }
        stream.Flush(flushToDisk: true);

        return path;
    }

    private static RelayConfig SandboxOn() =>
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
            InactivityTimeoutMsByTier: null,
            InactivityTimeoutMs: 600_000);
}
