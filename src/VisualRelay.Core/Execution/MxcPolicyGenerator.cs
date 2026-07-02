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
    /// separator, so no <c>process</c> block is emitted. Denials default to the full
    /// canonical set; the Windows launch path passes an existence-filtered set (see the
    /// overload).
    /// </summary>
    public static string Generate(string workspaceRoot, IReadOnlyList<string> cacheDirs)
        => Generate(workspaceRoot, cacheDirs, WindowsCredentialDenyDirs());

    /// <summary>
    /// Overload taking the explicit <paramref name="deniedDirs"/> to emit under
    /// <c>filesystem.deniedPaths</c>. The real Windows launch path
    /// (<see cref="MxcProvisioner"/>) passes an EXISTENCE-FILTERED set via
    /// <see cref="ExistingPaths"/>: MXC's DACL-mutation fallback (used when the
    /// BaseContainer backend is unavailable, e.g. a Windows Server host) stamps an ACE
    /// on every policy path and FAILS on one that does not exist ("os error 3"), so a
    /// stale denial would abort the whole run before any stage does work. Kept a
    /// parameter (not hardcoded) so this method stays a pure, OS-agnostic serializer
    /// that tests drive with explicit input.
    /// </summary>
    public static string Generate(
        string workspaceRoot, IReadOnlyList<string> cacheDirs, IReadOnlyList<string> deniedDirs)
    {
        var readwritePaths = new List<string> { workspaceRoot };
        readwritePaths.AddRange(cacheDirs);

        var policy = new
        {
            version = PinnedMxcVersion,
            filesystem = new { readwritePaths, deniedPaths = deniedDirs },
            network = new { defaultPolicy = "allow" },
        };
        return JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The canonical credential/secret locations VR marks as
    /// <c>filesystem.deniedPaths</c> — the Windows analogue of nono's
    /// <c>deny_credentials</c> group — as environment-variable placeholders. This is the
    /// full intent set (surfaced verbatim in the Settings panel); the policy actually
    /// emitted for a run is existence-filtered via <see cref="ExistingPaths"/> (see
    /// <see cref="MxcProvisioner"/>), because MXC's DACL-mutation fallback fails on a
    /// denied path that does not exist. Where MXC honors <c>deniedPaths</c> a denial
    /// takes precedence over the broader readwrite grants (a denied subtree under
    /// <c>%APPDATA%</c> is not re-opened by the coarse cache grant); where it does not
    /// yet, the Settings-panel Windows caveat warns the denial may be unenforced. Covers
    /// SSH/cloud/GPG/k8s/docker dotfiles and git/netrc secrets, the DPAPI master keys,
    /// the Credential Manager store, and Chromium profiles.
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

    /// <summary>
    /// The subset of <paramref name="paths"/> that ACTUALLY EXIST on disk, with
    /// environment variables expanded — the same existence discipline
    /// <see cref="DefaultWindowsCacheDirs"/> applies to readwrite roots, extended to the
    /// credential <c>deniedPaths</c>. MXC's DACL-mutation fallback stamps an ACE on every
    /// path and aborts the whole run on a missing one ("os error 3"), so denials must be
    /// filtered to existing paths before they reach the policy. Dropping an absent
    /// credential dir is safe: it has nothing to protect, and Windows MXC does not
    /// natively enforce <c>deniedPaths</c> in the pinned release anyway
    /// (<see cref="SandboxPathInspector.WindowsDeniedPathsEnforced"/>). Both files and
    /// directories count (e.g. <c>.git-credentials</c> / <c>.netrc</c> are files).
    /// </summary>
    public static IReadOnlyList<string> ExistingPaths(IEnumerable<string> paths) =>
        paths.Select(Environment.ExpandEnvironmentVariables)
            .Where(p => !string.IsNullOrWhiteSpace(p) && (Directory.Exists(p) || File.Exists(p)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
