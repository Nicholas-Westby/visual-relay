using System.Text.Json;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Windows credential-denial behaviour of the MXC policy: the generated policy
/// must carry the credential set under <c>filesystem.deniedPaths</c> (a pure
/// function, asserted on any OS). The inspector no longer has a Windows arm or a
/// "may be readable" caveat: on Windows it asks nono inside the WSL distro, where
/// the denials are enforced, so those tests left with it.
/// </summary>
public sealed class WindowsCredentialDenyTests
{
    // ── deniedPaths in the generated policy ──────────────────────────────

    [Fact]
    public void Generate_EmitsCredentialDeniedPaths_FromTheSingleHelper()
    {
        var json = MxcPolicyGenerator.Generate(@"C:\repo", []);

        using var doc = JsonDocument.Parse(json);
        var denied = doc.RootElement.GetProperty("filesystem")
            .GetProperty("deniedPaths").EnumerateArray()
            .Select(e => e.GetString()!).ToList();

        // The emitted set is exactly the single-source helper — no duplicated literal.
        Assert.Equal(MxcPolicyGenerator.WindowsCredentialDenyDirs().ToList(), denied);
        // Spot-check credential families the set must cover.
        Assert.Contains(@"%USERPROFILE%\.ssh", denied);
        Assert.Contains(@"%APPDATA%\Microsoft\Protect", denied);          // DPAPI master keys
        Assert.Contains(@"%LOCALAPPDATA%\Microsoft\Credentials", denied); // Credential Manager
        Assert.Contains(denied, p => p.Contains(@"Chrome\User Data"));    // browser data
    }

    [Fact]
    public void Generate_WithExplicitDeniedDirs_EmitsExactlyThose()
    {
        // The 3-arg overload is a pure serializer: whatever denied set it is given is
        // exactly what lands under filesystem.deniedPaths (the Windows launch path feeds
        // it an existence-filtered set; see MxcProvisioner.EnsurePolicy).
        var json = MxcPolicyGenerator.Generate(@"C:\repo", [], new[] { @"C:\x", @"C:\y" });

        using var doc = JsonDocument.Parse(json);
        var denied = doc.RootElement.GetProperty("filesystem")
            .GetProperty("deniedPaths").EnumerateArray()
            .Select(e => e.GetString()!).ToList();

        Assert.Equal(new[] { @"C:\x", @"C:\y" }, denied);
    }

    [Fact]
    public void ExistingPaths_KeepsExistingDropsMissing()
    {
        // MXC's DACL-mutation fallback aborts on a policy path that does not exist, so
        // credential denials are existence-filtered before they reach the policy.
        var existing = Path.Combine(
            Path.GetTempPath(), "vr-mxc-exist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(existing);
        try
        {
            var missing = Path.Combine(
                Path.GetTempPath(), "vr-mxc-missing-" + Guid.NewGuid().ToString("N"));
            var filtered = MxcPolicyGenerator.ExistingPaths(new[] { existing, missing });

            Assert.Contains(existing, filtered);
            Assert.DoesNotContain(missing, filtered);
        }
        finally
        {
            Directory.Delete(existing, recursive: true);
        }
    }

    [Fact]
    public void WindowsCredentialDenyDirs_CoversCredentialFamilies()
    {
        var dirs = MxcPolicyGenerator.WindowsCredentialDenyDirs();

        // SSH / cloud / GPG / k8s / docker dotfiles plus git/netrc secrets.
        Assert.Contains(@"%USERPROFILE%\.ssh", dirs);
        Assert.Contains(@"%USERPROFILE%\.aws", dirs);
        Assert.Contains(@"%USERPROFILE%\.azure", dirs);
        Assert.Contains(@"%USERPROFILE%\.gnupg", dirs);
        Assert.Contains(@"%USERPROFILE%\.kube", dirs);
        Assert.Contains(@"%USERPROFILE%\.docker", dirs);
        Assert.Contains(@"%USERPROFILE%\.git-credentials", dirs);
        Assert.Contains(@"%USERPROFILE%\.netrc", dirs);
        // OS credential stores plus Chromium browser profiles.
        Assert.Contains(@"%APPDATA%\Microsoft\Protect", dirs);
        Assert.Contains(@"%LOCALAPPDATA%\Microsoft\Credentials", dirs);
        Assert.Contains(dirs, p => p.Contains(@"Chrome\User Data"));
        Assert.Contains(dirs, p => p.Contains(@"Edge\User Data"));
    }
}
