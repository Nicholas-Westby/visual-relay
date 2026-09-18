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
    /// A child this small is copied even when the entry's copy budget is spent, because
    /// copying it costs nothing and linking it is what lets a write escape the snapshot.
    /// <para>
    /// Measured on i18next: <c>node_modules</c> has 395 children, and the budget was gone
    /// after the first fifteen alphabetically. Everything after <c>@rolldown</c> was linked
    /// however small it was, and the three glob patterns put every dot-named entry last, so
    /// <c>.bin</c>, <c>.vite</c> and <c>.vite-temp</c> were structurally guaranteed to lose.
    /// <c>.vite-temp</c> is 4 KB. Writing through the snapshot's copy of it landed the file
    /// in the real checkout, which verify mounts read-only, so vitest got EACCES before a
    /// test ran. A child's fate was decided by where its name fell in the alphabet.
    /// </para>
    /// </summary>
    internal const long FreeCopyBytes = 256 * 1024;

    /// <summary>
    /// The free size to use against <paramref name="thresholdBytes"/>. Clamped to a
    /// sixteenth of it, because a free size at or above the threshold would make every
    /// child that could be copied at all free, and the budget would stop existing.
    /// </summary>
    internal static long FreeBytesFor(long thresholdBytes) =>
        Math.Min(FreeCopyBytes, Math.Max(0, thresholdBytes / 16));

    /// <summary>
    /// <c>sh -c &lt;script&gt; vr-overlay &lt;source&gt; &lt;dest&gt; &lt;limit KiB&gt; &lt;free KiB&gt; &lt;name&gt;...</c>:
    /// the rule
    /// <c>RelayDriver.OverlayIgnoredDirRecursive</c> applies on the app side, so the two arms lay
    /// the same checkout out the same way and change together. An entry below the limit is copied
    /// with its modes and links (<c>cp -a</c>); a large file is linked to the source; a DIRECTORY
    /// at or above the limit becomes a real directory whose children are copied one by one until
    /// the entry's copy budget (the same limit) is spent, the rest linked — except that a child
    /// at or below the free size has its own budget, so a cheap one is never denied because
    /// bigger siblings came first in the glob (see <see cref="FreeCopyBytes"/>). Linking such a folder
    /// whole made every path under it the checkout's, which the sandbox mounts read-only for
    /// verify: vitest could not create <c>node_modules/.vite-temp</c> on i18next, printed EACCES
    /// and exited before a test ran. A destination that already exists (the checkout, the
    /// uncommitted overlay) is left alone.
    /// </summary>
    internal const string IgnoredEntriesScript =
        "src=$1; dst=$2; limit=$3; free=$4; shift 4; for name in \"$@\"; do "
        + "s=\"$src/$name\"; d=\"$dst/$name\"; "
        + "if [ -e \"$d\" ] || [ -L \"$d\" ]; then continue; fi; "
        + "mkdir -p \"$(dirname \"$d\")\" || { echo \"" + FailedPrefix + "$name\"; continue; }; "
        + "kb=$(du -sk \"$s\" 2>/dev/null | cut -f1); "
        + "if [ \"${kb:-0}\" -lt \"$limit\" ]; then cp -a \"$s\" \"$d\" || echo \"" + FailedPrefix + "$name\"; "
        + "elif [ ! -d \"$s\" ]; then ln -s \"$s\" \"$d\" || echo \"" + FailedPrefix + "$name\"; "
        + "elif ! mkdir \"$d\"; then echo \"" + FailedPrefix + "$name\"; "
        // The three patterns are every child including the dot-named ones; an unmatched pattern
        // stays literal in POSIX sh, so each candidate is tested for existence first.
        + "else copied=0; freed=0; for c in \"$s\"/* \"$s\"/.[!.]* \"$s\"/..?*; do "
        + "[ -e \"$c\" ] || [ -L \"$c\" ] || continue; b=${c##*/}; "
        + "ckb=$(du -sk \"$c\" 2>/dev/null | cut -f1); ckb=${ckb:-0}; take=0; "
        // A free-sized child is charged to its own budget, so it never spends what a
        // bigger sibling would have used and a bigger sibling never spends its.
        + "if [ \"$ckb\" -lt \"$limit\" ]; then "
        + "if [ \"$ckb\" -le \"$free\" ] && [ \"$freed\" -lt \"$limit\" ]; then take=1; freed=$((freed+ckb)); "
        + "elif [ \"$copied\" -lt \"$limit\" ]; then take=1; copied=$((copied+ckb)); fi; fi; "
        + "if [ \"$take\" = 1 ]; then cp -a \"$c\" \"$d/$b\" || echo \"" + FailedPrefix + "$name/$b\"; "
        + "else ln -s \"$c\" \"$d/$b\" || echo \"" + FailedPrefix + "$name/$b\"; fi; done; fi; done";

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
            (limitBytes / 1024).ToString(CultureInfo.InvariantCulture),
            (FreeBytesFor(limitBytes) / 1024).ToString(CultureInfo.InvariantCulture), .. names,
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
