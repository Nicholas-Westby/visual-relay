using System.IO.Compression;
using System.Text;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Unit tests for the MXC auto-provisioner's logic: it never downloads when wxc-exec
/// is already resolvable or when there is no consent, and it installs ONLY the
/// Windows x64 runtime binaries from the release zip. The real HTTP download is the
/// injected seam (proven empirically against the live release), so these stay
/// network-free and run on any OS.
/// </summary>
public sealed class MxcInstallerTests
{
    [Fact]
    public void Ensure_AlreadyResolvable_ReturnsPath_WithoutDownloading()
    {
        var path = MxcInstaller.Ensure(
            consent: true,
            log: _ => { },
            resolve: () => @"C:\already\wxc-exec.exe",
            download: _ => throw new Xunit.Sdk.XunitException("must not download when already present"),
            install: (_, _) => throw new Xunit.Sdk.XunitException("must not install when already present"));

        Assert.Equal(@"C:\already\wxc-exec.exe", path);
    }

    [Fact]
    public void Ensure_NotPresent_NoConsent_ReturnsNull_WithoutDownloading()
    {
        var path = MxcInstaller.Ensure(
            consent: false,
            log: _ => { },
            resolve: () => null,
            download: _ => throw new Xunit.Sdk.XunitException("must not download without consent"),
            install: (_, _) => throw new Xunit.Sdk.XunitException("must not install without consent"));

        Assert.Null(path);
    }

    [Fact]
    public void DownloadUrl_PinsTheReleaseAsset()
    {
        Assert.Contains(MxcInstaller.PinnedRelease, MxcInstaller.DownloadUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://github.com/microsoft/mxc/releases/download/", MxcInstaller.DownloadUrl);
        Assert.EndsWith("mxc-release-binaries.zip", MxcInstaller.DownloadUrl);
    }

    [Fact]
    public void InstallWindowsX64_ExtractsOnlyWindowsX64Binaries()
    {
        // A zip mirroring the real release layout: x64 Windows exes, an x64 Linux
        // binary (no .exe), x64 debug symbols, and an arm64 set.
        var zip = BuildZip(new Dictionary<string, string>
        {
            ["x64/wxc-exec.exe"] = "exe",
            ["x64/wxc-host-prep.exe"] = "prep",
            ["x64/lxc-exec"] = "linux",                 // not a Windows binary
            ["x64/symbols/wxc_exec.pdb"] = "pdb",        // debug symbol
            ["arm64/wxc-exec.exe"] = "arm",              // wrong arch
        });

        var dest = Path.Combine(Path.GetTempPath(), "vr-mxc-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            var count = MxcInstaller.InstallWindowsX64(zip, dest);

            Assert.Equal(2, count);
            Assert.True(File.Exists(Path.Combine(dest, "wxc-exec.exe")));
            Assert.True(File.Exists(Path.Combine(dest, "wxc-host-prep.exe")));
            Assert.Equal("exe", File.ReadAllText(Path.Combine(dest, "wxc-exec.exe")));
            Assert.False(File.Exists(Path.Combine(dest, "lxc-exec")));
            Assert.False(File.Exists(Path.Combine(dest, "wxc_exec.pdb")));
            // The arm64 wxc-exec must not clobber the x64 one.
            Assert.Equal("exe", File.ReadAllText(Path.Combine(dest, "wxc-exec.exe")));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    private static byte[] BuildZip(Dictionary<string, string> entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }
        return ms.ToArray();
    }
}
