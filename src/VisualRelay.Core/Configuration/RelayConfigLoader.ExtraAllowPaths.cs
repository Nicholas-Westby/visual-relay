using System.Text.Json;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Core.Configuration;

public static partial class RelayConfigLoader
{
    private static readonly string[] SensitiveHomeEntries =
    [
        ".ssh", ".gnupg", ".aws", ".config/gh", "Library/Keychains",
        ".bashrc", ".zshrc", ".profile", ".bash_profile", ".zprofile",
    ];

    /// <summary>
    /// Where a workspace's commands are sandboxed, as far as its allow-path entries care: a
    /// workspace opened inside a WSL distro runs its commands in that distro, so its entries
    /// name the distro's paths and resolve against the distro user's home; any other workspace
    /// is sandboxed on this machine. The distro is only resolved for a config that has entries.
    /// </summary>
    private static async Task<SandboxHost> SandboxHostForAsync(string rootPath, CancellationToken cancellationToken) =>
        WslPath.TryParseUnc(rootPath, out _, out _)
            ? SandboxHost.Windows(await WslContextResolver.TryGetCurrentAsync(cancellationToken).ConfigureAwait(false))
            : SandboxHost.Local;

    // The home, workspace and entry resolution one host checks entries with. Null Home: a distro
    // sandbox with no resolved distro, where nothing launches and no entry can be checked. Null
    // Root: a workspace that distro cannot reach, so only the home admits an entry.
    private sealed record AllowPathView(string? Home, string? Root, char Separator, Func<string, string?> Resolve);

    /// <summary>
    /// Reads <c>sandboxExtraAllowPaths</c>: each entry has <c>~</c> and <c>$HOME</c> expanded, must
    /// resolve under the sandbox's home or the workspace root, and must not reach a secret; the
    /// kept form is the one the sandbox's nono is given. An error is the Malformed diagnostic.
    /// </summary>
    private static async Task<(IReadOnlyList<string>? Paths, string? Error)> ReadExtraAllowPathsAsync(
        JsonElement root, string rootPath, string configPath,
        Func<string, CancellationToken, Task<SandboxHost>> sandboxHostFor, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("sandboxExtraAllowPaths", out var element))
            return (null, null);
        if (element.ValueKind != JsonValueKind.Array)
            return (null, $"relay config: sandboxExtraAllowPaths must be an array in {configPath}");

        var rawPaths = element.EnumerateArray()
            .Select(e => e.GetString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList();
        if (rawPaths.Count == 0)
            return ([], null);

        var view = ViewFor(await sandboxHostFor(rootPath, cancellationToken).ConfigureAwait(false), rootPath);
        if (view.Home is null)
            return ([], null);

        var validated = new List<string>(rawPaths.Count);
        foreach (var raw in rawPaths)
        {
            if (raw.Contains(".."))
                return (null, $"relay config: sandboxExtraAllowPaths entry contains '..' (path traversal rejected): \"{raw}\" in {configPath}");

            if (view.Resolve(raw) is not { } normalized)
                return (null, $"relay config: sandboxExtraAllowPaths entry is not a path the sandbox can reach, got: \"{raw}\" in {configPath}");

            if (!IsAtOrUnder(normalized, view.Home, view.Separator) && !IsAtOrUnder(normalized, view.Root, view.Separator))
                return (null, $"relay config: sandboxExtraAllowPaths entry must resolve under $HOME ({view.Home}) or workspace root ({view.Root}), got: \"{raw}\" → \"{normalized}\" in {configPath}");

            foreach (var entry in SensitiveHomeEntries)
            {
                var subtree = view.Home + view.Separator + entry.Replace('/', view.Separator);
                if (IsAtOrUnder(normalized, subtree, view.Separator))
                    return (null, $"relay config: sandboxExtraAllowPaths entry resolves into sensitive subtree \"{subtree}\": \"{raw}\" → \"{normalized}\" in {configPath}");
            }

            validated.Add(normalized);
        }

        return (validated, null);
    }

    private static AllowPathView ViewFor(SandboxHost host, string rootPath)
    {
        if (!host.IsWindows)
        {
            var home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return new AllowPathView(home, Path.GetFullPath(rootPath), Path.DirectorySeparatorChar,
                raw => Path.GetFullPath(ExpandHome(raw, home, Path.Combine)));
        }

        if (host.Wsl is null)
            return new AllowPathView(null, null, '/', _ => null);

        var distroHome = NormalizeLinux(host.Wsl.DistroHome);
        return new AllowPathView(distroHome, host.MapGrant(rootPath) is { } root ? NormalizeLinux(root) : null, '/',
            raw => host.MapGrant(ExpandHome(raw, distroHome, (home, rest) => home + "/" + rest)) is { } linux
                ? NormalizeLinux(linux)
                : null);
    }

    private static string ExpandHome(string raw, string home, Func<string, string, string> join) =>
        raw == "~" ? home
        : raw.StartsWith("~/") ? join(home, raw[2..].TrimStart('/'))
        : raw.Replace("$HOME", home, StringComparison.Ordinal);

    // Absolute Linux path with doubled separators, "." segments and a trailing slash dropped.
    private static string NormalizeLinux(string path) =>
        "/" + string.Join('/', path.Split('/').Where(segment => segment is not ("" or ".")));

    private static bool IsAtOrUnder(string path, string? directory, char separator) =>
        directory is not null && (path == directory || path.StartsWith(directory + separator, StringComparison.Ordinal));
}
