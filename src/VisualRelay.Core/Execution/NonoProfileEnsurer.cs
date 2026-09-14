using System.Text;
using VisualRelay.Core.Configuration;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Owns the nono <c>vr-guard</c> sandbox profile. The canonical content is
/// embedded in this assembly (single source of truth, identical to the repo's
/// <c>packaging/nono/vr-guard.json</c> the structure tests validate), written to
/// a VR-private XDG path, and <b>overwritten on every run</b> so it can never go
/// stale. nono then loads it by absolute path (<c>--profile &lt;path&gt;</c>),
/// not by the name <c>vr-guard</c> resolved from the global profiles dir.
///
/// <para>Rationale: the old launcher installed the profile under
/// <c>~/.config/nono/profiles/vr-guard.json</c> only-if-absent, so a machine
/// provisioned before the profile grew its toolchain-cache grants kept running
/// sandboxed builds under a stale, over-restrictive copy — denied writes stalled
/// the agent until the test cap fired. VR owns this private file; per-repo or
/// extra access is the separate <c>sandboxExtraAllowPaths</c> seam, so there is
/// nothing of the user's to preserve here and overwrite-always is correct.</para>
///
/// <para>On Windows nono runs inside the WSL distro, so the profile is placed
/// there (<see cref="WslProfilePlacement"/>): written from this side through the
/// distro's UNC share, loaded by nono through its Linux path. Which placement a call
/// means is decided by the <see cref="SandboxHost"/> it is handed, and only a call
/// handed none asks what this machine is.</para>
/// </summary>
public static class NonoProfileEnsurer
{
    /// <summary>Manifest name pinned via <c>LogicalName</c> in the csproj.</summary>
    private const string ResourceName = "VisualRelay.Core.vr-guard.json";

    /// <summary>The profile's directory under the config root, on every platform.</summary>
    internal const string DirName = "visual-relay";
    internal const string FileName = "vr-guard.json";

    private static string? _cachedContent;

    /// <summary>
    /// The embedded profile content (UTF-8 text, byte-for-byte equal to the
    /// repo's <c>packaging/nono/vr-guard.json</c>). Cached after first read.
    /// </summary>
    public static string EmbeddedContent => _cachedContent ??= ReadEmbedded();

    /// <summary>
    /// Resolves the absolute path nono loads the profile from, as nono on
    /// <paramref name="host"/> sees it (null: this machine, <see cref="SandboxHost.Current"/>).
    /// On the local host that is VR's owned <c>$XDG_CONFIG_HOME/visual-relay/vr-guard.json</c>
    /// (default <c>$HOME/.config/visual-relay/vr-guard.json</c>), beside VR's
    /// <c>.env</c>, reusing <see cref="XdgConfig"/>'s XDG/HOME resolution and its
    /// injectable accessor; throws when neither <c>XDG_CONFIG_HOME</c> nor
    /// <c>HOME</c> is set. On a host with a WSL context it is the Linux path of the
    /// copy placed inside the distro. Never reads <see cref="SandboxHost.ProfilePath"/>,
    /// which is answered here.
    /// </summary>
    public static string ResolveProfilePath(IEnvironmentAccessor? accessor = null, SandboxHost? host = null)
    {
        if ((host ?? SandboxHost.Current).Wsl is { } context)
            return WslProfilePlacement.For(context.Distro, context.DistroHome).LinuxPath;

        var configDir = XdgConfig.ResolveConfigDir(accessor);
        return Path.Combine(configDir, DirName, FileName);
    }

    /// <summary>
    /// Writes the embedded profile where nono on <paramref name="host"/> loads it
    /// (null: this machine, resolved without blocking). On the local host that is the
    /// resolved XDG path, creating the parent directory if needed. <b>Overwrite-always</b>:
    /// the file is made to match the embedded content even if it was hand-edited; the
    /// actual write is skipped only when the on-disk bytes already match (avoids mtime
    /// churn). Returns the resolved absolute path on success. Throws an actionable
    /// <see cref="InvalidOperationException"/> when the path cannot be resolved or
    /// the write fails — the run must NOT proceed to a sandboxed stage with a
    /// missing or stale profile. On a host with a WSL context the profile goes inside
    /// the distro instead (<see cref="EnsureInDistroAsync"/>) and the Linux path is
    /// returned; a Windows host with no resolved distro has nowhere to put it and throws.
    /// </summary>
    public static Task<string> EnsureAsync(
        IEnvironmentAccessor? accessor = null, SandboxHost? host = null, CancellationToken cancellationToken = default) =>
        EnsureAsync(accessor, host, WriteThroughShareAsync, cancellationToken);

    /// <summary>
    /// <see cref="EnsureAsync(IEnvironmentAccessor?, SandboxHost?, CancellationToken)"/> with the
    /// share write injected, so which arm a host selects is exercised on any OS without a share.
    /// </summary>
    internal static async Task<string> EnsureAsync(
        IEnvironmentAccessor? accessor, SandboxHost? host,
        Func<string, string, CancellationToken, Task> writeThroughShare, CancellationToken cancellationToken)
    {
        var resolved = host ?? await SandboxHost.CurrentAsync(cancellationToken);
        if (resolved.Wsl is { } context)
            return await EnsureInDistroAsync(context, writeThroughShare, cancellationToken);
        if (resolved.IsWindows)
            throw new InvalidOperationException(
                "The vr-guard sandbox profile is placed inside the WSL distro, but no usable WSL2 distro was "
                + "resolved. Run `visual-relay launch`: its gate names what is missing (WSL, a WSL2 distro, nono "
                + "inside it, Landlock).");

        string path;
        try
        {
            path = ResolveProfilePath(accessor, resolved);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Cannot resolve the vr-guard sandbox profile path "
                + "($XDG_CONFIG_HOME/visual-relay/vr-guard.json): neither XDG_CONFIG_HOME "
                + "nor HOME is set. Set HOME so the always-on sandbox profile can be written.", ex);
        }

        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var dirExisted = Directory.Exists(dir);
            if (!dirExisted)
            {
                Directory.CreateDirectory(dir);
                // A local host on Windows is one a test stated, and Windows has no Unix mode.
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(dir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var desired = EmbeddedContent;
            // Skip the write only when bytes already match (no mtime churn).
            if (!File.Exists(path)
                || !string.Equals(await File.ReadAllTextAsync(path, cancellationToken), desired, StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(path, desired, cancellationToken);
            }

            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to write the vr-guard sandbox profile to '{path}'. "
                + "VR will not run a sandboxed stage with a missing or stale profile. "
                + $"Check filesystem permissions on that path. ({ex.Message})", ex);
        }
    }

    /// <summary>
    /// The Windows arm: writes the embedded profile through the distro's UNC share
    /// (<see cref="WslProfilePlacement"/>) and returns the Linux path nono loads.
    /// Overwrite-always, unconditionally: a share round trip just to compare bytes
    /// is not worth the mtime it would save. The write is injected so the arm is
    /// exercised without a share; the real writer is <see cref="WriteThroughShareAsync"/>.
    /// </summary>
    internal static async Task<string> EnsureInDistroAsync(
        WslContext context, Func<string, string, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var (writePath, linuxPath) = WslProfilePlacement.For(context.Distro, context.DistroHome);
        try
        {
            await write(writePath, EmbeddedContent, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to write the vr-guard sandbox profile to '{writePath}' inside the WSL distro "
                + $"'{context.Distro}'. VR will not run a sandboxed stage with a missing or stale profile. The "
                + @"distro's files are reached through its \\wsl.localhost share, which exists only while "
                + "[automount] enabled=true (the default) in the distro's /etc/wsl.conf; after changing it run "
                + $"`wsl --shutdown` and start the distro again. ({ex.Message})", ex);
        }

        return linuxPath;
    }

    private static async Task WriteThroughShareAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private static string ReadEmbedded()
    {
        var assembly = typeof(NonoProfileEnsurer).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded nono profile '{ResourceName}' was not found in "
                + $"{assembly.GetName().Name}. The build must embed packaging/nono/vr-guard.json.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
