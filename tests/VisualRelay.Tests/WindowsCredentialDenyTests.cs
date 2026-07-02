using System.Text.Json;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Windows credential-denial behaviour. The generated MXC policy must carry the
/// credential set under <c>filesystem.deniedPaths</c> (a pure function, asserted
/// on any OS), and the Windows inspector result must surface those denials with a
/// Windows-only "may not be enforced" caveat — a caveat the macOS/Linux (nono)
/// result must NOT carry, since nono genuinely enforces the denials.
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

    // ── Windows caveat surfacing + scoping ───────────────────────────────

    [Fact]
    public void BuildWindowsResult_SurfacesDeniedPathsInBlocked()
    {
        var result = SandboxPathInspector.BuildWindowsResult(@"C:\repo", null);

        // Every credential deny dir shows up as a Blocked entry in the panel.
        foreach (var dir in MxcPolicyGenerator.WindowsCredentialDenyDirs())
            Assert.Contains(result.BlockedPaths, e => e.Raw == dir);
    }

    [Fact]
    public void BuildWindowsResult_CaveatTracksEnforcementFlag()
    {
        var result = SandboxPathInspector.BuildWindowsResult(@"C:\repo", null);
        // Read the const into a local so flipping it stays a one-line production
        // change that keeps this test green (no unreachable-branch warning).
        var enforced = SandboxPathInspector.WindowsDeniedPathsEnforced;

        if (enforced)
        {
            // Once MXC enforces deniedPaths, the caveat must disappear entirely.
            Assert.Null(result.WindowsCredentialCaveat);
            Assert.Null(result.WindowsCredentialCaveatUrl);
            return;
        }

        // Default (not yet enforced): honest, conservative wording — not a guarantee.
        Assert.False(string.IsNullOrWhiteSpace(result.WindowsCredentialCaveat));
        Assert.Contains("denied", result.WindowsCredentialCaveat!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("readable", result.WindowsCredentialCaveat!, StringComparison.OrdinalIgnoreCase);

        // A real, absolute https tracking link to the MXC filesystem-policy work.
        Assert.True(Uri.TryCreate(result.WindowsCredentialCaveatUrl, UriKind.Absolute, out var uri));
        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        Assert.Contains("github.com/microsoft/mxc", result.WindowsCredentialCaveatUrl!);
    }

    [Fact]
    public void NonoResult_CarriesNoCaveat()
    {
        // The macOS/Linux (nono) builder output must never carry the Windows caveat:
        // nono enforces deny_credentials, so a "not enforced" note would misinform.
        var nono = SandboxPathInspector.BuildResult(
        [
            new("/", "/", SandboxAccess.ReadOnly, "vr-guard"),
            new("$HOME/.ssh", "/home/u/.ssh", SandboxAccess.Blocked, "deny_credentials"),
        ]);

        Assert.Null(nono.WindowsCredentialCaveat);
        Assert.Null(nono.WindowsCredentialCaveatUrl);
    }

    // ── Platform-aware reads/writes summary ──────────────────────────────

    [Fact]
    public void BuildWindowsResult_ReadsSummarySaysReadsUnrestricted()
    {
        var result = SandboxPathInspector.BuildWindowsResult(@"C:\repo", null);

        Assert.False(string.IsNullOrWhiteSpace(result.ReadsSummary));
        // Windows reads are not restricted, so the summary must NOT repeat the nono
        // "except the blocked paths" phrasing that would misdescribe MXC.
        Assert.Contains("not restricted", result.ReadsSummary!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("except the blocked", result.ReadsSummary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonoResult_ReadsSummaryKeepsBlockedPathsException()
    {
        var nono = SandboxPathInspector.BuildResult(
            [new SandboxPathEntry("/", "/", SandboxAccess.ReadOnly, "vr-guard")]);

        Assert.False(string.IsNullOrWhiteSpace(nono.ReadsSummary));
        // macOS/Linux genuinely block the deny paths, so the summary keeps the
        // "except the blocked paths" wording — distinct from the Windows text.
        Assert.Contains("except the blocked paths", nono.ReadsSummary!, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(
            SandboxPathInspector.BuildWindowsResult("/ws", null).ReadsSummary, nono.ReadsSummary);
    }
}
