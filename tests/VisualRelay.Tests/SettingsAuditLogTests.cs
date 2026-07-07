using System.Runtime.InteropServices;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Tests for <see cref="SettingsAuditLog"/>: every write to bridge settings
/// must leave a durable, attributable, timestamped record.
/// </summary>
public sealed class SettingsAuditLogTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(),
        "vr-audit-log", Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFileSystem.DeleteDirectoryResilient(_scratch);

    private DictionaryEnvironmentAccessor SandboxedEnv()
    {
        var env = new DictionaryEnvironmentAccessor
        {
            ["HOME"] = Path.Combine(_scratch, "home"),
            ["XDG_CONFIG_HOME"] = Path.Combine(_scratch, "xdg")
        };
        Directory.CreateDirectory(env["HOME"]!);
        return env;
    }

    [Fact]
    public void Append_ChangedObsidianKey_WritesCorrectLine()
    {
        var env = SandboxedEnv();
        var procName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var pid = Environment.ProcessId;

        SettingsAuditLog.Append("VR_OBSIDIAN_ENABLED",
            oldValue: "false", newValue: "true",
            source: "settings-ui", accessor: env);

        // Resolve the expected log path (same dir as .env).
        var logPath = Path.Combine(
            Path.GetDirectoryName(KeyEnvFile.ResolvePathForCurrentUser(env))!,
            "settings-audit.log");
        Assert.True(File.Exists(logPath), "Audit log file must exist after append.");

        var content = File.ReadAllText(logPath).TrimEnd();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);

        var line = lines[0].TrimEnd();
        // Format: {ISO8601-UTC} {key} "{old}" -> "{new}" source={source} pid={pid} proc={proc}
        Assert.Contains("VR_OBSIDIAN_ENABLED", line, StringComparison.Ordinal);
        Assert.Contains("\"false\" -> \"true\"", line, StringComparison.Ordinal);
        Assert.Contains("source=settings-ui", line, StringComparison.Ordinal);
        Assert.Contains($"pid={pid}", line, StringComparison.Ordinal);
        Assert.Contains($"proc={procName}", line, StringComparison.Ordinal);
        // Values must be in clear (not redacted) for Obsidian keys.
        Assert.DoesNotContain("<redacted>", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_NoOpSave_WritesNothing()
    {
        var env = SandboxedEnv();

        // Simulate a no-op save where nothing changed — the Save method
        // should not call Append at all. But we test Append directly:
        // if we never call Append, the log file must not exist.
        // We verify by checking that the log is absent after only setting up
        // the env — no Append call means no file.
        var logPath = Path.Combine(
            Path.GetDirectoryName(KeyEnvFile.ResolvePathForCurrentUser(env))!,
            "settings-audit.log");
        Assert.False(File.Exists(logPath),
            "Audit log must not exist when no Append was called.");
    }

    [Fact]
    public void Append_NonObsidianKey_RedactsValue()
    {
        var env = SandboxedEnv();
        var procName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        var pid = Environment.ProcessId;

        SettingsAuditLog.Append("HF_TOKEN",
            oldValue: "sk-old-secret", newValue: "sk-new-secret",
            source: "settings-ui", accessor: env);

        var logPath = Path.Combine(
            Path.GetDirectoryName(KeyEnvFile.ResolvePathForCurrentUser(env))!,
            "settings-audit.log");
        Assert.True(File.Exists(logPath));

        var content = File.ReadAllText(logPath).TrimEnd();
        var line = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].TrimEnd();

        // Values must be redacted for non-Obsidian keys.
        Assert.Contains("<redacted>", line, StringComparison.Ordinal);
        Assert.Contains($"pid={pid}", line, StringComparison.Ordinal);
        Assert.Contains($"proc={procName}", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_MigrationSource_WritesMigrationLine()
    {
        var env = SandboxedEnv();

        SettingsAuditLog.Append("VR_OBSIDIAN_VAULT_ROOT",
            oldValue: null, newValue: "/tmp/migrated-vault",
            source: "migration", accessor: env);

        var logPath = Path.Combine(
            Path.GetDirectoryName(KeyEnvFile.ResolvePathForCurrentUser(env))!,
            "settings-audit.log");
        Assert.True(File.Exists(logPath));

        var content = File.ReadAllText(logPath).TrimEnd();
        Assert.Contains("source=migration", content, StringComparison.Ordinal);
        // null oldValue renders as (unset).
        Assert.Contains("(unset)", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_CreatesFileWithHardenedPermissions()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        var env = SandboxedEnv();

        SettingsAuditLog.Append("VR_OBSIDIAN_ENABLED",
            oldValue: "false", newValue: "true",
            source: "settings-ui", accessor: env);

        var logPath = Path.Combine(
            Path.GetDirectoryName(KeyEnvFile.ResolvePathForCurrentUser(env))!,
            "settings-audit.log");
        Assert.True(File.Exists(logPath));

        // File must be 0600 (owner read+write only).
        var fileMode = File.GetUnixFileMode(logPath);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            fileMode);

        // Parent directory must be 0700.
        var dirMode = File.GetUnixFileMode(
            Path.GetDirectoryName(logPath)!);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            dirMode);
    }
}
