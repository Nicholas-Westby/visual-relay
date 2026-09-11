namespace VisualRelay.Core.Execution.Wsl;

/// <summary>
/// Where the vr-guard profile lives on Windows. nono runs inside the distro, so
/// the profile must be readable there by a Linux path; VR writes it from the
/// Windows side through the distro's <c>\\wsl.localhost</c> share. One call
/// yields both views of the same file, so they cannot drift apart.
/// </summary>
public static class WslProfilePlacement
{
    /// <summary>(the UNC path to write through, the Linux path nono loads) for the distro user's home.</summary>
    public static (string WritePath, string LinuxPath) For(string distro, string distroHome)
    {
        if (string.IsNullOrEmpty(distroHome) || distroHome[0] != '/')
            throw new ArgumentException($"The distro home must be an absolute Linux path, got '{distroHome}'.", nameof(distroHome));

        // The same place the Unix arm resolves through XdgConfig: the XDG default
        // config root under the user's home, then VR's private directory.
        var linuxPath = $"{distroHome.TrimEnd('/')}/.config/{NonoProfileEnsurer.DirName}/{NonoProfileEnsurer.FileName}";
        return (WslPath.ToUnc(distro, linuxPath), linuxPath);
    }
}
