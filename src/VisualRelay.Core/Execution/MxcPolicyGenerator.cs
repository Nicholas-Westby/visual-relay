using System.Text.Json;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Generates the Visual-Relay-authored Microsoft Execution Containers (MXC) policy
/// that wraps swival on Windows — the analogue of the nono <c>vr-guard</c> profile.
/// VR hand-authors this policy (never the SDK's auto-generated one, which can be
/// over-permissive): writes are confined to the workspace plus the same
/// per-ecosystem toolchain caches vr-guard grants, reads are broad (the agent must
/// read the system), and outbound network stays open (swival must reach the LiteLLM
/// proxy and providers; Windows MXC would not filter it anyway). The pinned MXC
/// version is a single constant so flipping to a newer release is one edit.
/// </summary>
public static class MxcPolicyGenerator
{
    /// <summary>Pinned MXC config schema version the policy targets (one-edit upgrade).</summary>
    public const string PinnedMxcVersion = "0.7.0-alpha";

    /// <summary>
    /// Emits the confined-write policy JSON in the real MXC v0.7.0-alpha schema
    /// (verified against <c>wxc-exec</c>): writes confined under
    /// <c>filesystem.readwritePaths</c> = workspace root followed by
    /// <paramref name="cacheDirs"/>; reads stay broad by MXC default (no
    /// <c>readonlyPaths</c> needed); and <c>network.defaultPolicy = "allow"</c> opts
    /// back into outbound-open (MXC is deny-by-default since SDK 0.3.0), so swival can
    /// reach the LiteLLM proxy. The command is supplied at launch via the <c>--</c>
    /// separator, so no <c>process</c> block is emitted.
    /// </summary>
    public static string Generate(string workspaceRoot, IReadOnlyList<string> cacheDirs)
    {
        var readwritePaths = new List<string> { workspaceRoot };
        readwritePaths.AddRange(cacheDirs);

        var policy = new
        {
            version = PinnedMxcVersion,
            filesystem = new { readwritePaths, deniedPaths = WindowsCredentialDenyDirs() },
            network = new { defaultPolicy = "allow" },
        };
        return JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The credential/secret locations the policy marks as
    /// <c>filesystem.deniedPaths</c> — the Windows analogue of nono's
    /// <c>deny_credentials</c> group. Emitted as environment-variable
    /// placeholders and NOT existence-filtered: denials of absent paths are
    /// harmless and forward-compatible. Where MXC honors <c>deniedPaths</c> it
    /// takes precedence over the broader readwrite grants (a denied subtree under
    /// <c>%APPDATA%</c> is not re-opened by the coarse cache grant); where it does
    /// not yet, the Settings-panel Windows caveat warns the denial may be
    /// unenforced. Covers SSH/cloud/GPG/k8s/docker dotfiles and git/netrc secrets,
    /// the DPAPI master keys, the Credential Manager store, and Chromium profiles.
    /// </summary>
    public static IReadOnlyList<string> WindowsCredentialDenyDirs() => new[]
    {
        @"%USERPROFILE%\.ssh",
        @"%USERPROFILE%\.aws",
        @"%USERPROFILE%\.azure",
        @"%USERPROFILE%\.gnupg",
        @"%USERPROFILE%\.kube",
        @"%USERPROFILE%\.docker",
        @"%USERPROFILE%\.git-credentials",
        @"%USERPROFILE%\.netrc",
        @"%APPDATA%\Microsoft\Protect",
        @"%LOCALAPPDATA%\Microsoft\Credentials",
        @"%LOCALAPPDATA%\Google\Chrome\User Data",
        @"%LOCALAPPDATA%\Microsoft\Edge\User Data",
    };

    /// <summary>
    /// The Windows toolchain cache directories granted read+write, mirroring
    /// vr-guard's allow-list: <c>%LOCALAPPDATA%</c> and <c>%APPDATA%</c> (NuGet, uv,
    /// npm, etc. live under these), the user-profile package caches, and the scratch
    /// temp dir. Only dirs that ACTUALLY EXIST are returned: MXC's AppContainer+DACL
    /// backend stamps an ACE on each readwrite root, and a non-existent path makes the
    /// whole container setup fail (verified against wxc-exec) — so e.g. a missing
    /// <c>~/.cargo</c> on a non-Rust host must not reach the policy.
    /// </summary>
    public static IReadOnlyList<string> DefaultWindowsCacheDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = new List<string>
        {
            localAppData,
            appData,
            Path.Combine(home, ".nuget", "packages"),
            Path.Combine(home, ".dotnet"),
            Path.Combine(home, ".cargo"),
            Path.Combine(home, ".config", "swival"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Unity"),
            Path.GetTempPath(),
        };
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
