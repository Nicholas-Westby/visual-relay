using System.Text;
using VisualRelay.Core.Execution;
using VisualRelay.Domain;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <see cref="UserEnvSnapshot"/> loading and
/// <see cref="SwivalSubagentRunner.BuildTargetCommandEnvironment"/> merge semantics.
/// </summary>
public sealed class UserEnvSnapshotTests : IDisposable
{
    private readonly DictionaryEnvironmentAccessor _env = new();
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles) { try { File.Delete(f); } catch { /* best-effort temp-file cleanup */ } }
    }

    // ── UserEnvSnapshot.Load ──

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
        Assert.Null(UserEnvSnapshot.Load(_env));
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsNull()
    {
        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = "/nonexistent/path/user-env";
        Assert.Null(UserEnvSnapshot.Load(_env));
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyDict()
    {
        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = WriteSnapshot();
        var result = UserEnvSnapshot.Load(_env);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    // ── BuildTargetCommandEnvironment ──

    [Fact]
    public void BuildTargetCommandEnvironment_WithSnapshot_MergesUserBaseWithVROverrides()
    {
        var snapshotPath = WriteSnapshot(
            ("VR_SNAP_MARKER", "present"),
            ("PYTHONDONTWRITEBYTECODE", "0"),
            ("SOME_USER_VAR", "user-value"),
            ("PATH", "/usr/bin:/bin"));
        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshotPath;

        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env);

        Assert.NotNull(result);
        var overrides = result.Overrides;
        Assert.Equal("present", overrides["VR_SNAP_MARKER"]);
        Assert.Equal("user-value", overrides["SOME_USER_VAR"]);
        Assert.Equal("1", overrides["PYTHONDONTWRITEBYTECODE"]); // VR wins on collision
        Assert.Equal("1", overrides["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("1", overrides["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.False(overrides.ContainsKey("SDKROOT"));
    }

    [Fact]
    public void BuildTargetCommandEnvironment_WithoutSnapshot_FallsBackToSandboxOverridesOnly()
    {
        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env);

        Assert.NotNull(result);
        var overrides = result.Overrides;
        Assert.Equal("1", overrides["PYTHONDONTWRITEBYTECODE"]);
        Assert.Equal("1", overrides["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("1", overrides["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.False(overrides.ContainsKey("VR_SNAP_MARKER"));
        Assert.Empty(result.Remove);
    }

    [Fact]
    public void BuildTargetCommandEnvironment_SnapshotDoesNotReintroduceNixOnlyVars()
    {
        var snapshotPath = WriteSnapshot(
            ("HOME", "/home/user"),
            ("PATH", "/usr/local/bin:/usr/bin:/bin"),
            ("TMPDIR", "/var/folders/ab/cdefg/T"));
        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshotPath;

        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env);

        Assert.NotNull(result);
        var overrides = result.Overrides;
        Assert.False(overrides.ContainsKey("SDKROOT"));
        Assert.False(overrides.ContainsKey("DEVELOPER_DIR"));
        Assert.Equal("/home/user", overrides["HOME"]);
        Assert.Equal("/var/folders/ab/cdefg/T", overrides["TMPDIR"]);
    }

    [Fact]
    public void BuildTargetCommandEnvironment_DevshellOnlyKey_IsInRemove()
    {
        var processEnv = new Dictionary<string, string>
        {
            ["DOTNET_ROOT"] = "/nix/store/x/share/dotnet",
            ["HOME"] = "/home/user",
            ["PATH"] = "/usr/local/bin:/usr/bin:/bin",
        };
        var snapshotPath = WriteSnapshot(("HOME", "/home/user"), ("PATH", "/usr/local/bin:/usr/bin:/bin"));
        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshotPath;

        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env, processEnv);

        Assert.NotNull(result);
        Assert.Contains("DOTNET_ROOT", result.Remove);
        Assert.False(result.Overrides.ContainsKey("DOTNET_ROOT"));
    }

    [Fact]
    public void BuildTargetCommandEnvironment_SnapshotKey_NotInRemove()
    {
        var processEnv = new Dictionary<string, string>
        {
            ["HOME"] = "/nix/store/override-home",
            ["PATH"] = "/usr/bin:/bin",
        };
        var snapshotPath = WriteSnapshot(("HOME", "/home/user"), ("PATH", "/usr/bin:/bin"));
        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshotPath;

        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env, processEnv);

        Assert.NotNull(result);
        Assert.DoesNotContain("HOME", result.Remove);
        Assert.Equal("/home/user", result.Overrides["HOME"]);
    }

    [Fact]
    public void BuildTargetCommandEnvironment_VrOverrideKeys_NeverInRemove()
    {
        var processEnv = new Dictionary<string, string>
        {
            ["HOME"] = "/home/user",
            ["PATH"] = "/usr/bin:/bin",
            ["SHELL"] = "/bin/bash",
        };
        var snapshotPath = WriteSnapshot(("HOME", "/home/user"), ("PATH", "/usr/bin:/bin"));
        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshotPath;

        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env, processEnv);

        Assert.NotNull(result);
        Assert.Equal("1", result.Overrides["PYTHONDONTWRITEBYTECODE"]);
        Assert.Equal("1", result.Overrides["MSBUILDDISABLENODEREUSE"]);
        Assert.Equal("1", result.Overrides["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.DoesNotContain("PYTHONDONTWRITEBYTECODE", result.Remove);
        Assert.DoesNotContain("MSBUILDDISABLENODEREUSE", result.Remove);
        Assert.DoesNotContain("DOTNET_CLI_TELEMETRY_OPTOUT", result.Remove);
        Assert.Contains("SHELL", result.Remove);
    }

    [Fact]
    public void BuildTargetCommandEnvironment_NoSnapshot_RemoveEmpty()
    {
        var sandboxEnv = SwivalSubagentRunner.BuildSandboxEnvironment(SandboxOn());
        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env);

        Assert.NotNull(result);
        Assert.Empty(result.Remove);
        foreach (var kvp in sandboxEnv)
            Assert.Equal(kvp.Value, result.Overrides[kvp.Key]);
        Assert.Equal(sandboxEnv.Count, result.Overrides.Count);
    }

    [Fact]
    public void BuildTargetCommandEnvironment_SnapshotWithoutPath_TreatedAsInvalid()
    {
        var snapshotPath = WriteSnapshot(("HOME", "/home/user"), ("USER", "testuser"));
        _env["VISUAL_RELAY_USER_ENV_SNAPSHOT"] = snapshotPath;
        var sandboxEnv = SwivalSubagentRunner.BuildSandboxEnvironment(SandboxOn());
        var result = SwivalSubagentRunner.BuildTargetCommandEnvironment(SandboxOn(), _env);

        Assert.NotNull(result);
        Assert.Empty(result.Remove);
        foreach (var kvp in sandboxEnv)
            Assert.Equal(kvp.Value, result.Overrides[kvp.Key]);
        Assert.Equal(sandboxEnv.Count, result.Overrides.Count);
    }

    // ── Helpers ──

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

    private static RelayConfig SandboxOn() => new(
        "llm-tasks", "true", "true", [],
        new Dictionary<string, string> { ["cheap"] = "cheap" },
        true, 1, 1, false, true, 0, 300_000,
        new Dictionary<string, int> { ["cheap"] = 90_000, ["balanced"] = 120_000, ["frontier"] = 660_000 },
        660_000, InactivityTimeoutMsByTier: null, InactivityTimeoutMs: 600_000);
}
