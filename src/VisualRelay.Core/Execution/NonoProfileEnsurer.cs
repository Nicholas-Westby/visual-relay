using System.Text;
using VisualRelay.Core.Configuration;

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
/// </summary>
public static class NonoProfileEnsurer
{
    /// <summary>Manifest name pinned via <c>LogicalName</c> in the csproj.</summary>
    private const string ResourceName = "VisualRelay.Core.vr-guard.json";

    private const string DirName = "visual-relay";
    private const string FileName = "vr-guard.json";

    private static string? _cachedContent;

    /// <summary>
    /// The embedded profile content (UTF-8 text, byte-for-byte equal to the
    /// repo's <c>packaging/nono/vr-guard.json</c>). Cached after first read.
    /// </summary>
    public static string EmbeddedContent => _cachedContent ??= ReadEmbedded();

    /// <summary>
    /// Resolves the absolute path of VR's owned profile —
    /// <c>$XDG_CONFIG_HOME/visual-relay/vr-guard.json</c> (default
    /// <c>$HOME/.config/visual-relay/vr-guard.json</c>) — beside VR's <c>.env</c>,
    /// reusing <see cref="XdgConfig"/>'s XDG/HOME resolution and its injectable
    /// accessor. Throws when neither <c>XDG_CONFIG_HOME</c> nor <c>HOME</c> is set.
    /// </summary>
    public static string ResolveProfilePath(IEnvironmentAccessor? accessor = null)
    {
        var configDir = XdgConfig.ResolveConfigDir(accessor);
        return Path.Combine(configDir, DirName, FileName);
    }

    /// <summary>
    /// Writes the embedded profile to the resolved XDG path, creating the parent
    /// directory if needed. <b>Overwrite-always</b>: the file is made to match the
    /// embedded content even if it was hand-edited; the actual write is skipped
    /// only when the on-disk bytes already match (avoids mtime churn). Returns the
    /// resolved absolute path on success. Throws an actionable
    /// <see cref="InvalidOperationException"/> when the path cannot be resolved or
    /// the write fails — the run must NOT proceed to a sandboxed stage with a
    /// missing or stale profile.
    /// </summary>
    public static async Task<string> EnsureAsync(
        IEnvironmentAccessor? accessor = null, CancellationToken cancellationToken = default)
    {
        string path;
        try
        {
            path = ResolveProfilePath(accessor);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Cannot resolve the vr-guard sandbox profile path "
                + "($XDG_CONFIG_HOME/visual-relay/vr-guard.json): neither XDG_CONFIG_HOME "
                + "nor HOME is set. Set HOME or disable the sandbox (bypassSandbox).", ex);
        }

        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
            {
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
