using System.Runtime.Versioning;
using System.Security.Cryptography;
using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Tests;

/// <summary>
/// The shell <c>setup-wsl</c> runs inside the distro, run here under <c>/bin/sh</c> with
/// stand-ins for the Debian tools (dpkg, apt-get, curl, wslpath) that record what they
/// were asked to do. What matters most is the nono package's checksum: a package whose
/// SHA-256 is not the pinned one must never reach apt, whether it was downloaded or given
/// as a local file.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class WslSetupScriptTests : IDisposable
{
    private const string Pinned = "nono-cli_0.75.0_amd64.deb";

    private readonly string _root = Directory.CreateTempSubdirectory("vr-setup-scripts-").FullName;
    private string Bin => Path.Combine(_root, "bin");
    private string Calls => Path.Combine(_root, "calls.log");
    private string Installed => Path.Combine(_root, "installed.deb");
    private string Package => Path.Combine(_root, Pinned);

    public WslSetupScriptTests()
    {
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateDirectory(Bin);
        File.WriteAllBytes(Package, RandomNumberGenerator.GetBytes(4096));
        Shim("dpkg", "echo \"${VR_TEST_ARCH:-amd64}\"");
        // printf, not echo: macOS sh's echo turns the "\n" in C:\Temp\nono... into a newline.
        // apt-get keeps a copy of the package it was given, so a test can say WHICH bytes reached it.
        Shim("apt-get", $"printf '%s\\n' \"apt-get $*\" >> '{Calls}'; for a; do last=$a; done; cp \"$last\" '{Installed}'");
        Shim("curl", $"printf '%s\\n' \"curl $*\" >> '{Calls}'; while [ $# -gt 0 ]; do [ \"$1\" = -o ] && out=$2; shift; done; cp '{Package}' \"$out\"");
        Shim("wslpath", $"printf '%s\\n' \"wslpath $*\" >> '{Calls}'; printf '%s\\n' '{Package}'");
        if (PathExecutables.Find("sha256sum") is null)
            Shim("sha256sum", "exec shasum -a 256 \"$@\"");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    public static TheoryData<string> Scripts => new(nameof(WslSetupScripts.CreateUser), nameof(WslSetupScripts.Packages), nameof(WslSetupScripts.Nono));

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task EveryScript_IsValidShell(string name)
    {
        SkipOnWindows();
        var script = name switch
        {
            nameof(WslSetupScripts.CreateUser) => WslSetupScripts.CreateUser,
            nameof(WslSetupScripts.Packages) => WslSetupScripts.Packages,
            _ => WslSetupScripts.Nono,
        };

        var (exitCode, output, _) = await ProcessCapture.RunAsync("/bin/sh", ["-n", "-c", script], _root, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(exitCode == 0, output);
    }

    [Fact]
    public async Task ALocalPackageNamedByItsWindowsPath_IsInstalled_WhenItsChecksumIsThePinnedOne()
    {
        SkipOnWindows();

        var (exitCode, output) = await RunNonoAsync(amd64Sha: Sha(Package), local: @"C:\Temp\" + Pinned);

        Assert.True(exitCode == 0, output);
        Assert.Contains(@"wslpath -u C:\Temp\" + Pinned, File.ReadAllText(Calls));
        Assert.Contains("apt-get -q -y -o DPkg::Lock::Timeout=300 install --no-install-recommends ", File.ReadAllText(Calls));
        Assert.Equal(File.ReadAllBytes(Package), File.ReadAllBytes(Installed));
    }

    [Fact]
    public async Task APackageWithOneByteChanged_IsRefused_AndNeverReachesApt()
    {
        SkipOnWindows();
        var pinned = Sha(Package);
        var bytes = File.ReadAllBytes(Package);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(Package, bytes);

        var (exitCode, output) = await RunNonoAsync(amd64Sha: pinned, local: Package);

        Assert.Equal(4, exitCode);
        Assert.Contains($"not the pinned {pinned}", output);
        Assert.DoesNotContain("apt-get", ReadCalls());
        Assert.False(File.Exists(Installed));
    }

    [Fact]
    public async Task WithNoLocalPackage_TheReleaseDownloadForTheDistrosArchitecture_IsInstalled()
    {
        SkipOnWindows();

        var (exitCode, output) = await RunNonoAsync(arm64Sha: Sha(Package), arch: "arm64");

        Assert.True(exitCode == 0, output);
        Assert.Contains($" {NonoRelease.DebUrlPrefix}arm64.deb", ReadCalls());
        Assert.Equal(File.ReadAllBytes(Package), File.ReadAllBytes(Installed));
    }

    [Fact]
    public async Task AnArchitectureNonoPublishesNoPackageFor_IsRefusedBeforeAnyDownload()
    {
        SkipOnWindows();

        var (exitCode, output) = await RunNonoAsync(arch: "riscv64");

        Assert.Equal(3, exitCode);
        Assert.Contains("riscv64", output);
        Assert.Equal("", ReadCalls());
    }

    private async Task<(int ExitCode, string Output)> RunNonoAsync(
        string amd64Sha = NonoRelease.Amd64DebSha256, string arm64Sha = NonoRelease.Arm64DebSha256,
        string local = "", string arch = "amd64")
    {
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = $"{Bin}:{Environment.GetEnvironmentVariable("PATH")}",
            ["VR_TEST_ARCH"] = arch,
        };
        var (exitCode, output, _) = await ProcessCapture.RunAsync("/bin/sh",
            ["-c", WslSetupScripts.Nono, "vr-setup", NonoRelease.Version, amd64Sha, arm64Sha, NonoRelease.DebUrlPrefix, local],
            _root, TimeSpan.FromSeconds(30), CancellationToken.None, environment);
        return (exitCode, output);
    }

    private string ReadCalls() => File.Exists(Calls) ? File.ReadAllText(Calls) : "";

    private void Shim(string name, string body)
    {
        var path = Path.Combine(Bin, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void SkipOnWindows() =>
        Assert.SkipWhen(OperatingSystem.IsWindows(), "runs the distro's scripts under /bin/sh, which Windows has no stand-in for");
}
