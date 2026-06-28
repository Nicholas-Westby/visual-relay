using System.IO.Compression;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Self-provisions the MXC runtime the way the Windows launcher provisions
/// .NET/uv/swival: a no-op when <c>wxc-exec</c> is already resolvable, otherwise —
/// with consent — it downloads the pinned, Microsoft-signed release from
/// <c>microsoft/mxc</c> and installs the Windows x64 binaries (wxc-exec plus its
/// sandbox daemon/guest/host-prep helpers) into the per-user cache slot
/// <see cref="MxcProvisioner.ResolveWxcExec"/> checks. The download and the extraction
/// are injectable so the skip/consent logic and the x64-only filtering are unit-tested
/// without the network.
/// </summary>
public static class MxcInstaller
{
    /// <summary>The pinned microsoft/mxc release (one-edit upgrade).</summary>
    public const string PinnedRelease = "v0.7.0-rc1";

    /// <summary>The signed Windows release-asset URL for the pinned release.</summary>
    public static string DownloadUrl =>
        $"https://github.com/microsoft/mxc/releases/download/{PinnedRelease}/mxc-release-binaries.zip";

    // The Windows x64 runtime binaries the AppContainer/processcontainer backend needs;
    // the zip also carries arm64, debug symbols, an SBOM, and Linux/macOS binaries we skip.
    private static readonly string[] WindowsBinaries =
    [
        "wxc-exec.exe", "wxc-windows-sandbox-daemon.exe", "wxc-windows-sandbox-guest.exe",
        "wxc-host-prep.exe", "winhttp-proxy-shim.exe", "mxc-diagnostic-console.exe",
    ];

    /// <summary>
    /// Ensures wxc-exec is available, returning its path (or null). No-op when already
    /// resolvable; otherwise requires <paramref name="consent"/> before downloading —
    /// never a silent network fetch. The seams default to a real HTTP download and the
    /// x64 extraction; tests inject them.
    /// </summary>
    public static string? Ensure(
        bool consent,
        Action<string> log,
        Func<string?>? resolve = null,
        Func<string, byte[]>? download = null,
        Func<byte[], string, int>? install = null)
    {
        resolve ??= MxcProvisioner.ResolveWxcExec;
        if (resolve() is { } existing)
            return existing;
        if (!consent)
        {
            log("MXC (wxc-exec) is not installed; skipping provisioning (no consent to download).");
            return null;
        }

        download ??= DefaultDownload;
        install ??= InstallWindowsX64;
        var cacheDir = Path.GetDirectoryName(MxcProvisioner.CachedWxcExecPath())!;
        Directory.CreateDirectory(cacheDir);
        log($"provisioning MXC {PinnedRelease} (one-time) into {cacheDir}");
        try
        {
            var count = install(download(DownloadUrl), cacheDir);
            log($"installed {count} MXC binaries");
        }
        catch (Exception ex)
        {
            log($"MXC provisioning failed: {ex.Message}");
            return null;
        }
        return File.Exists(MxcProvisioner.CachedWxcExecPath()) ? MxcProvisioner.CachedWxcExecPath() : null;
    }

    /// <summary>
    /// Extracts ONLY the Windows x64 runtime binaries from the release zip into
    /// <paramref name="destDir"/> (skipping the arm64 set, debug symbols, the SBOM,
    /// and the Linux/macOS binaries). Returns the count installed.
    /// </summary>
    public static int InstallWindowsX64(byte[] zipBytes, string destDir)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var count = 0;
        foreach (var entry in zip.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith("x64/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!WindowsBinaries.Contains(Path.GetFileName(normalized), StringComparer.OrdinalIgnoreCase))
                continue;
            entry.ExtractToFile(Path.Combine(destDir, Path.GetFileName(normalized)), overwrite: true);
            count++;
        }
        return count;
    }

    private static byte[] DefaultDownload(string url)
    {
        // ReSharper disable once ShortLivedHttpClient — one-off download in installer; socket exhaustion not material
        // ReSharper disable once UsingStatementResourceInitialization
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        return http.GetByteArrayAsync(url).GetAwaiter().GetResult();
    }
}
