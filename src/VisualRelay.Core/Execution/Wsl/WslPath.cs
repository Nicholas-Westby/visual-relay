using System.Text;

namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// The one place Windows and Linux views of a WSL path are translated. A distro's
/// filesystem is reachable from Windows as <c>\\wsl$\&lt;distro&gt;\...</c> (legacy)
/// or <c>\\wsl.localhost\&lt;distro&gt;\...</c>; a Windows drive is reachable from
/// inside the distro under <c>/mnt/&lt;letter&gt;/...</c> (DrvFs, default automount
/// root). Pure string work: spaces and non-ASCII pass through untouched, because
/// wsl.exe and the 9P server do the UTF-16/UTF-8 conversion.
/// </summary>
public static class WslPath
{
    private const string LegacyHost = "wsl$";
    private const string Host = "wsl.localhost";
    private const string MntRoot = "/mnt/";

    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// Parses a WSL UNC path (either host, either slash direction) into the distro
    /// name and the absolute Linux path; trailing and doubled separators are dropped.
    /// Rejects relative paths, other UNC hosts, an empty distro and any <c>..</c>
    /// segment (the picker must never hand VR a path that escapes what was chosen).
    /// </summary>
    public static bool TryParseUnc(string path, out string distro, out string linuxPath)
    {
        distro = string.Empty;
        linuxPath = string.Empty;
        if (string.IsNullOrEmpty(path) || path.Length < 2 || !IsSeparator(path[0]) || !IsSeparator(path[1]))
            return false;

        var segments = path[2..].Split(Separators);
        if (segments.Length < 2)
            return false;
        var host = segments[0];
        if (!host.Equals(LegacyHost, StringComparison.OrdinalIgnoreCase)
            && !host.Equals(Host, StringComparison.OrdinalIgnoreCase))
            return false;

        var name = segments[1];
        if (name.Length == 0 || name is "." or "..")
            return false;

        var rest = new List<string>();
        foreach (var segment in segments.Skip(2))
        {
            if (segment.Length == 0)
                continue;
            if (segment == "..")
                return false;
            rest.Add(segment);
        }

        distro = name;
        linuxPath = "/" + string.Join('/', rest);
        return true;
    }

    /// <summary>
    /// Renders the modern UNC form (<c>\\wsl.localhost\&lt;distro&gt;\...</c>) of an
    /// absolute Linux path inside <paramref name="distro"/>; the distro root has no
    /// trailing separator. Throws on a relative path, a <c>..</c> segment or an
    /// empty distro rather than producing a UNC path that points somewhere else.
    /// </summary>
    public static string ToUnc(string distro, string linuxPath)
    {
        if (string.IsNullOrEmpty(distro) || distro.IndexOfAny(Separators) >= 0)
            throw new ArgumentException("A WSL distro name is required.", nameof(distro));
        if (string.IsNullOrEmpty(linuxPath) || linuxPath[0] != '/')
            throw new ArgumentException($"An absolute Linux path is required, got '{linuxPath}'.", nameof(linuxPath));

        var unc = new StringBuilder(@"\\").Append(Host).Append('\\').Append(distro);
        foreach (var segment in linuxPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
                throw new ArgumentException($"A '..' segment is not allowed in '{linuxPath}'.", nameof(linuxPath));
            unc.Append('\\').Append(segment);
        }

        return unc.ToString();
    }

    /// <summary>
    /// Translates an absolute Windows drive path (<c>C:\x y\ü</c>) to its DrvFs
    /// mount inside the distro (<c>/mnt/c/x y/ü</c>, drive letter lowercased).
    /// Rejects drive-relative (<c>C:x</c>), rooted-without-drive (<c>\x</c>),
    /// relative and UNC paths.
    /// </summary>
    public static bool TryDriveToMnt(string windowsPath, out string linuxPath)
    {
        linuxPath = string.Empty;
        if (string.IsNullOrEmpty(windowsPath) || windowsPath.Length < 3
            || !char.IsAsciiLetter(windowsPath[0]) || windowsPath[1] != ':' || !IsSeparator(windowsPath[2]))
            return false;

        var segments = windowsPath[3..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var mount = MntRoot + char.ToLowerInvariant(windowsPath[0]);
        linuxPath = segments.Length == 0 ? mount : mount + "/" + string.Join('/', segments);
        return true;
    }

    /// <summary>
    /// True when <paramref name="linuxPath"/> is a Windows drive seen through DrvFs:
    /// <c>/mnt/&lt;letter&gt;</c> or anything below it. <c>/mnt/wsl</c> and other
    /// multi-letter entries under <c>/mnt</c> are not drives.
    /// </summary>
    public static bool IsMntPath(string linuxPath)
    {
        if (string.IsNullOrEmpty(linuxPath) || !linuxPath.StartsWith(MntRoot, StringComparison.Ordinal))
            return false;

        var rest = linuxPath[MntRoot.Length..];
        if (rest.Length == 0 || !char.IsAsciiLetter(rest[0]))
            return false;
        return rest.Length == 1 || rest[1] == '/';
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
