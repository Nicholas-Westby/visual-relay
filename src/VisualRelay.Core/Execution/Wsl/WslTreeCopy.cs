using System.Globalization;

namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// The verify snapshot's copies on the Windows arm, run inside the distro. A copy the app
/// makes through the <c>\\wsl.localhost</c> share is created 0644 by the share's server, so
/// an executable in an ignored dependency dir (<c>vendor/bin/phpunit</c>,
/// <c>node_modules/.bin/jest</c>, <c>.venv/bin/pytest</c>) stops being runnable, and a link
/// the app creates there points at a Windows path Linux cannot follow. Linux <c>cp</c> and
/// <c>ln</c> keep modes and write real links; one wsl.exe per batch also beats a Windows
/// round trip per file.
/// </summary>
internal static class WslTreeCopy
{
    /// <summary>A line a script prints for a name it could not place; the rest of the line is the name.</summary>
    private const string FailedPrefix = "vr-overlay-failed ";

    /// <summary>
    /// <c>sh -c &lt;script&gt; vr-overlay &lt;source&gt; &lt;dest&gt; &lt;limit KiB&gt; &lt;name&gt;...</c>: each
    /// ignored entry below the limit is copied with its modes and links (<c>cp -a</c>); one at or
    /// above it is linked to the source, as the local overlay does. A destination that already
    /// exists (the checkout, the uncommitted overlay) is left alone.
    /// </summary>
    internal const string IgnoredEntriesScript =
        "src=$1; dst=$2; limit=$3; shift 3; for name in \"$@\"; do "
        + "s=\"$src/$name\"; d=\"$dst/$name\"; "
        + "if [ -e \"$d\" ] || [ -L \"$d\" ]; then continue; fi; "
        + "mkdir -p \"$(dirname \"$d\")\" || { echo \"" + FailedPrefix + "$name\"; continue; }; "
        + "kb=$(du -sk \"$s\" 2>/dev/null | cut -f1); "
        + "if [ \"${kb:-0}\" -lt \"$limit\" ]; then cp -a \"$s\" \"$d\" || echo \"" + FailedPrefix + "$name\"; "
        + "else ln -s \"$s\" \"$d\" || echo \"" + FailedPrefix + "$name\"; fi; done";

    /// <summary>
    /// <c>sh -c &lt;script&gt; vr-overlay &lt;source&gt; &lt;dest&gt; &lt;relative path&gt;...</c>: each file is
    /// copied over the checkout with its mode (<c>cp -p</c>), creating parent directories.
    /// </summary>
    internal const string FilesScript =
        "src=$1; dst=$2; shift 2; for rel in \"$@\"; do "
        + "mkdir -p \"$(dirname \"$dst/$rel\")\" && cp -p \"$src/$rel\" \"$dst/$rel\" 2>/dev/null "
        + "|| echo \"" + FailedPrefix + "$rel\"; done";

    private const string ScriptName = "vr-overlay";

    /// <summary>The launch that overlays <paramref name="names"/> (relative to the source) into the destination.</summary>
    internal static WslLaunch IgnoredEntries(
        WslContext context, string linuxSource, string linuxDest, long limitBytes, IReadOnlyList<string> names) =>
        WslLauncher.BuildPlain(context.WslExePath, context.Distro,
        [
            "/bin/sh", "-c", IgnoredEntriesScript, ScriptName, linuxSource, linuxDest,
            (limitBytes / 1024).ToString(CultureInfo.InvariantCulture), .. names,
        ]);

    /// <summary>The launch that copies <paramref name="relativePaths"/> from the source over the destination.</summary>
    internal static WslLaunch Files(
        WslContext context, string linuxSource, string linuxDest, IReadOnlyList<string> relativePaths) =>
        WslLauncher.BuildPlain(context.WslExePath, context.Distro,
            ["/bin/sh", "-c", FilesScript, ScriptName, linuxSource, linuxDest, .. relativePaths]);

    /// <summary>
    /// The distro context and Linux paths when both <paramref name="sourcePath"/> and
    /// <paramref name="destPath"/> are shares of <paramref name="host"/>'s distro; null when
    /// either is not (the local host, another distro, a drive path), which keeps the app-side copy.
    /// </summary>
    internal static (WslContext Context, string Source, string Dest)? InDistro(
        SandboxHost host, string sourcePath, string destPath)
    {
        if (host.Wsl is not { } context
            || !WslPath.TryParseUnc(sourcePath, out var sourceDistro, out var linuxSource)
            || !WslPath.TryParseUnc(destPath, out var destDistro, out var linuxDest)
            || !sourceDistro.Equals(context.Distro, StringComparison.OrdinalIgnoreCase)
            || !destDistro.Equals(context.Distro, StringComparison.OrdinalIgnoreCase))
            return null;
        return (context, linuxSource, linuxDest);
    }

    /// <summary>The names a script reported it could not place, in order.</summary>
    internal static IReadOnlyList<string> FailedNames(string output) =>
        output.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith(FailedPrefix, StringComparison.Ordinal))
            .Select(line => line[FailedPrefix.Length..])
            .ToList();
}
