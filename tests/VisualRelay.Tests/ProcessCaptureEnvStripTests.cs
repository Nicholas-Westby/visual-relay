using System.Diagnostics;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Guards that <see cref="ProcessCapture"/> strips VR's leaked nix apple-sdk env
/// (DEVELOPER_DIR/SDKROOT, exported by <c>nix develop</c> for VR's own .NET build)
/// from spawned child processes. Without this, swival (which runs <c>git</c> inside
/// nono) and the verify test command invoke <c>/usr/bin/git</c> — the macOS xcrun
/// shim — with a nix-store DEVELOPER_DIR, which has no Command Line Tools, popping
/// the "install the command line developer tools" dialog mid-run.
/// </summary>
public sealed class ProcessCaptureEnvStripTests
{
    [Fact]
    public void StripLeakedNixSdkEnv_RemovesNixPathedDeveloperDirAndSdkroot()
    {
        var env = new ProcessStartInfo().EnvironmentVariables;
        env["DEVELOPER_DIR"] = "/nix/store/abc-apple-sdk-14.4";
        env["SDKROOT"] = "/nix/store/abc-apple-sdk-14.4/Platforms/MacOSX.platform/Developer/SDKs/MacOSX.sdk";

        ProcessCapture.StripLeakedNixSdkEnv(env);

        Assert.False(env.ContainsKey("DEVELOPER_DIR"));
        Assert.False(env.ContainsKey("SDKROOT"));
    }

    [Fact]
    public void StripLeakedNixSdkEnv_PreservesRealXcodeDeveloperDir()
    {
        var env = new ProcessStartInfo().EnvironmentVariables;
        env["DEVELOPER_DIR"] = "/Applications/Xcode.app/Contents/Developer";

        ProcessCapture.StripLeakedNixSdkEnv(env);

        Assert.Equal("/Applications/Xcode.app/Contents/Developer", env["DEVELOPER_DIR"]);
    }

    [Fact]
    public void StripLeakedNixSdkEnv_NoOpWhenAbsent()
    {
        var env = new ProcessStartInfo().EnvironmentVariables;
        env.Remove("DEVELOPER_DIR");
        env.Remove("SDKROOT");

        ProcessCapture.StripLeakedNixSdkEnv(env); // must not throw

        Assert.False(env.ContainsKey("DEVELOPER_DIR"));
        Assert.False(env.ContainsKey("SDKROOT"));
    }
}
