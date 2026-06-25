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
    /// <summary>Pinned MXC release the policy targets (flip in one place to upgrade).</summary>
    public const string PinnedMxcVersion = "0.7.0";

    /// <summary>
    /// Emits the confined-write policy JSON: <c>readwritePaths</c> = workspace root
    /// followed by <paramref name="cacheDirs"/>, broad <paramref name="readonlyRoots"/>,
    /// and <c>network.allowOutbound = true</c>.
    /// </summary>
    public static string Generate(
        string workspaceRoot, IReadOnlyList<string> cacheDirs, IReadOnlyList<string> readonlyRoots)
    {
        var readwritePaths = new List<string> { workspaceRoot };
        readwritePaths.AddRange(cacheDirs);

        var policy = new
        {
            version = PinnedMxcVersion,
            readonlyPaths = readonlyRoots,
            readwritePaths,
            network = new { allowOutbound = true },
        };
        return JsonSerializer.Serialize(policy, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The Windows toolchain cache directories granted read+write, mirroring
    /// vr-guard's allow-list: <c>%LOCALAPPDATA%</c> and <c>%APPDATA%</c> (NuGet, uv,
    /// npm, etc. live under these), the user-profile package caches, and the scratch
    /// temp dir. Resolved against the real environment.
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
            Path.GetTempPath(),
        };
        return dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The broad read roots: every fixed/local drive the agent may read.</summary>
    public static IReadOnlyList<string> DefaultWindowsReadonlyRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType is DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception)
        {
            return [@"C:\"];
        }
    }
}
