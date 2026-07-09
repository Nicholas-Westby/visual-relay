using VisualRelay.App.ViewModels;
using VisualRelay.Core.Configuration;

namespace VisualRelay.Tests;

/// <summary>
/// Hermeticity and regression tests for the Obsidian bridge settings path.
/// - Hydration must never persist (construction is a no-op on disk).
/// - Genuine property changes must persist only changed keys.
/// - The old leaky pattern (empty accessor + fixture vault root) must not
///   corrupt a seeded .env.
/// - The suite-wide <c>XDG_CONFIG_HOME</c> redirect must be active.
/// </summary>
[Collection("Headless")]
public sealed class ObsidianBridgeHermeticityTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(),
        "vr-hermeticity", Guid.NewGuid().ToString("N"));

    public void Dispose() => TestFileSystem.DeleteDirectoryResilient(_scratch);

    /// <summary>
    /// Create a sandboxed accessor (HOME + XDG_CONFIG_HOME in scratch) and
    /// ensure the config directory exists.
    /// </summary>
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

    /// <summary>
    /// Seed a .env file with the given bridge settings so the VM's
    /// LoadObsidianBridgeSettings hydrates from known state.
    /// </summary>
    private string SeedEnvFile(DictionaryEnvironmentAccessor env,
        bool enabled, string vaultRoot, int pollSeconds)
    {
        var path = KeyEnvFile.ResolvePathForCurrentUser(env);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path,
            $"VR_OBSIDIAN_ENABLED={(enabled ? "true" : "false")}\n" +
            $"VR_OBSIDIAN_VAULT_ROOT={vaultRoot}\n" +
            $"VR_OBSIDIAN_POLL_SECONDS={pollSeconds}\n");
        return path;
    }

    [AvaloniaFact]
    public void ConstructingViewModel_AgainstSeededEnv_LeavesFileByteIdentical()
    {
        var env = SandboxedEnv();
        var envPath = SeedEnvFile(env, enabled: true,
            vaultRoot: "/tmp/custom-vault", pollSeconds: 45);
        var originalBytes = File.ReadAllBytes(envPath);

        // Construction alone must not write anything to disk.
        _ = new MainWindowViewModel(environmentAccessor: env);

        Assert.True(File.Exists(envPath));
        var afterBytes = File.ReadAllBytes(envPath);
        Assert.Equal(originalBytes, afterBytes);
    }

    [AvaloniaFact]
    public void GenuinePropertyChange_PersistsOnlyChangedKey()
    {
        var env = SandboxedEnv();
        // Seed with known values — we'll hydrate the VM to match, then change one key.
        var envPath = SeedEnvFile(env, enabled: false,
            vaultRoot: "/tmp/custom-vault", pollSeconds: 45);

        var vm = new MainWindowViewModel(environmentAccessor: env);
        // Hydrate the VM to match the seeded file (each set persists, but
        // after hydration the file matches the VM state).
        vm.ObsidianPollSeconds = 45; // write first to avoid overwriting
        vm.ObsidianVaultRoot = "/tmp/custom-vault";
        vm.ObsidianEnabled = false;

        // Now change only ObsidianEnabled.
        vm.ObsidianEnabled = true;

        var afterContent = File.ReadAllText(envPath);
        // The enabled key should have changed, but the vault root and poll
        // seconds must still be their original values.
        Assert.Contains("VR_OBSIDIAN_ENABLED=true", afterContent,
            StringComparison.Ordinal);
        Assert.Contains("VR_OBSIDIAN_VAULT_ROOT=/tmp/custom-vault",
            afterContent, StringComparison.Ordinal);
        Assert.Contains("VR_OBSIDIAN_POLL_SECONDS=45", afterContent,
            StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void Regression_EmptyAccessorWithFixtureVaultRoot_DoesNotCorruptSeededEnv()
    {
        var env = SandboxedEnv();
        var envPath = SeedEnvFile(env, enabled: true,
            vaultRoot: "/tmp/custom-vault", pollSeconds: 60);
        var originalBytes = File.ReadAllBytes(envPath);

        // Simulate the old leaky pattern: empty accessor + fixture vault root.
        // With the hermetic change, an empty accessor can't resolve config dir
        // so the persist is a no-op (InvalidOperationException caught by Save).
        var leakyEnv = new DictionaryEnvironmentAccessor();
        _ = new MainWindowViewModel(environmentAccessor: leakyEnv)
        {
            ObsidianVaultRoot = "/Users/dev/obsidian-vault"
        };

        // The seeded .env must be untouched.
        Assert.True(File.Exists(envPath));
        var afterBytes = File.ReadAllBytes(envPath);
        Assert.Equal(originalBytes, afterBytes);
    }

    [AvaloniaFact]
    public void XdgConfigHome_IsRedirectedToTempDir()
    {
        // Assert against the value the module initializer RECORDED at load, not
        // the live env var: a concurrent test in the ProcessEnv collection nulls
        // and restores the process-wide XDG_CONFIG_HOME under a try/finally, and a
        // live read here (a different, parallel collection) can land in that window.
        var xdg = TestModuleInitializer.RedirectedXdgConfigHome;
        Assert.NotNull(xdg);
        Assert.NotEmpty(xdg);

        // Should NOT start with the real home directory.
        var realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(xdg.StartsWith(realHome, StringComparison.OrdinalIgnoreCase));

        // Should contain something temp-ish.
        Assert.Contains(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
            xdg, StringComparison.OrdinalIgnoreCase);
    }
}
