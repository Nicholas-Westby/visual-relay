using System.Diagnostics;

namespace VisualRelay.Tests;

/// <summary>
/// Cheap, sub-second <c>nono why</c> oracle assertions. These run by default
/// when <c>nono</c> is on PATH and are skipped with a message when it is absent.
/// They verify that each toolchain cache path is ALLOWED for read+write under
/// the <c>vr-guard</c> profile, and that the destructive surface stays DENIED.
/// Uses the source profile file so the tests are self-consistent and do not
/// depend on the installed profile being up-to-date.
/// </summary>
public sealed class NonoWhyOracleTests
{
    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static bool NonoAvailable =>
        !string.IsNullOrEmpty(FindOnPath("nono"));

    private static string? _profilePath;

    /// <summary>
    /// Path to the vr-guard profile in the source tree, resolved lazily.
    /// Uses the source file rather than the installed profile so the tests
    /// are self-consistent and do not depend on deployment state.
    /// </summary>
    private static string ProfilePath
    {
        get
        {
            if (_profilePath is not null)
                return _profilePath;
            _profilePath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "packaging", "nono", "vr-guard.json"));
            return _profilePath;
        }
    }

    // ── Allowed paths ──────────────────────────────────────────────────

    [Fact]
    public void NonoWhy_NuGetPackages_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertAllowed(Path.Combine(Home, ".nuget", "packages"));
    }

    [Fact]
    public void NonoWhy_DotNetHome_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertAllowed(Path.Combine(Home, ".dotnet"));
    }

    [Fact]
    public void NonoWhy_SwiftPm_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertAllowed(Path.Combine(Home, ".swiftpm"));
    }

    [Fact]
    public void NonoWhy_Npm_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertAllowed(Path.Combine(Home, ".npm"));
    }

    [Fact]
    public void NonoWhy_NixCache_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertAllowed(Path.Combine(Home, ".cache", "nix"));
    }

    [Fact]
    public void NonoWhy_NixState_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertAllowed(Path.Combine(Home, ".local", "state", "nix"));
    }

    // ── Denied paths (destructive surface stays denied) ─────────────────

    [Fact]
    public void NonoWhy_Documents_DeniedWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertDenied(Path.Combine(Home, "Documents"), "write");
    }

    [Fact]
    public void NonoWhy_Desktop_DeniedWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertDenied(Path.Combine(Home, "Desktop"), "write");
    }

    [Fact]
    public void NonoWhy_SshCredentials_DeniedWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertDenied(Path.Combine(Home, ".ssh"), "write");
    }

    [Fact]
    public void NonoWhy_NixStore_DeniedWrite()
    {
        // The store stays read-only (read+exec via read:["/"]); the daemon
        // performs the privileged store writes. A direct write must be DENIED.
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertDenied(Path.Combine("/nix", "store", "x"), "write");
    }

    // ── OS-specific cache paths ────────────────────────────────────────

    [Fact]
    public void NonoWhy_MacOsSwiftPmCache_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("Test is macOS-only (SwiftPM ~/Library cache)");
        AssertAllowed(Path.Combine(Home, "Library", "Caches", "org.swift.swiftpm"));
    }

    [Fact]
    public void NonoWhy_LinuxSwiftPmCache_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Test is Linux-only (SwiftPM ~/.cache)");
        AssertAllowed(Path.Combine(Home, ".cache", "org.swift.swiftpm"));
    }

    [Fact]
    public void NonoWhy_MacOsPipCache_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        if (!OperatingSystem.IsMacOS()) Assert.Skip("Test is macOS-only");
        AssertAllowed(Path.Combine(Home, "Library", "Caches", "pip"));
    }

    [Fact]
    public void NonoWhy_LinuxPipCache_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        if (!OperatingSystem.IsLinux()) Assert.Skip("Test is Linux-only");
        AssertAllowed(Path.Combine(Home, ".cache", "pip"));
    }

    [Fact]
    public void NonoWhy_LinuxGoModCache_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        if (!OperatingSystem.IsLinux()) Assert.Skip("Test is Linux-only");
        AssertAllowed(Path.Combine(Home, "go", "pkg", "mod"));
    }

    [Fact]
    public void NonoWhy_LinuxGoBuildCache_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        if (!OperatingSystem.IsLinux()) Assert.Skip("Test is Linux-only");
        AssertAllowed(Path.Combine(Home, ".cache", "go-build"));
    }

    // ── Narrowed-allowlist regression: removed from ~/.local and ~/Library/Developer ──

    [Fact]
    public void NonoWhy_LocalBin_DeniedWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertDenied(Path.Combine(Home, ".local", "bin", "x"), "write");
    }

    [Fact]
    public void NonoWhy_ProvisioningProfiles_DeniedWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("Test is macOS-only (Provisioning Profiles under ~/Library/Developer)");
        AssertDenied(Path.Combine(Home, "Library", "Developer", "Xcode", "UserData",
            "Provisioning Profiles", "x"), "write");
    }

    [Fact]
    public void NonoWhy_LocalShareUv_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        if (!OperatingSystem.IsLinux())
            Assert.Skip("Test is Linux-only (uv data under ~/.local/share/uv)");
        AssertAllowed(Path.Combine(Home, ".local", "share", "uv"));
    }

    [Fact]
    public void NonoWhy_LocalShareNuGet_AllowedReadWrite()
    {
        if (!NonoAvailable) Assert.Skip("nono is not on PATH");
        AssertAllowed(Path.Combine(Home, ".local", "share", "NuGet"));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static void AssertAllowed(string path)
    {
        var (allowed, output) = RunNonoWhy(path, "readwrite");
        Assert.True(allowed,
            $"EXPECTED ALLOWED but was DENIED: nono why -p {ProfilePath} --op readwrite --path {path}\n{output}");
    }

    private static void AssertDenied(string path, string op)
    {
        var (allowed, output) = RunNonoWhy(path, op);
        Assert.False(allowed,
            $"EXPECTED DENIED but was ALLOWED: nono why -p {ProfilePath} --op {op} --path {path}\n{output}");
    }

    private static (bool IsAllowed, string Output) RunNonoWhy(string path, string op)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            try { Directory.CreateDirectory(path); }
            catch { /* can't create — skip the oracle check gracefully */ }
        }

        var psi = new ProcessStartInfo("nono",
            $"why -p \"{ProfilePath}\" --op {op} --path \"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        try
        {
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(10_000);
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            return (!output.Contains("DENIED", StringComparison.Ordinal), output);
        }
        catch (Exception ex)
        {
            return (false, $"nono why failed: {ex.Message}");
        }
    }

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        return pathEnv.Split(sep)
            .Select(dir => Path.Combine(dir.Trim(), name))
            .FirstOrDefault(File.Exists);
    }
}
