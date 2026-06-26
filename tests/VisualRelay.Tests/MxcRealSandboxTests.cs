using System.Diagnostics;
using VisualRelay.Core.Execution;

namespace VisualRelay.Tests;

/// <summary>
/// Empirical Phase-3 "step zero": runs VR's REAL <see cref="MxcPolicyGenerator"/> +
/// <see cref="WindowsSandbox.BuildMxcLaunch"/> against the actually-installed
/// <c>wxc-exec</c>, proving the generated policy is accepted AND that writes are
/// confined to the workspace on Windows. Gated to Windows with wxc-exec provisioned,
/// so it skips on macOS/Linux and on CI hosts without MXC (where the unit tests in
/// <see cref="WindowsSandboxTests"/> cover the policy shape and launch wiring).
/// </summary>
public sealed class MxcRealSandboxTests
{
    private static (int Exit, string Output) RunMxc(string wxc, string policyPath, string fileToWrite)
    {
        // `cmd /c echo x> <file>` — the redirect's write is what MXC governs.
        var (fileName, args) = WindowsSandbox.BuildMxcLaunch(
            wxc, policyPath, "cmd", new[] { "/c", $"echo vr> {fileToWrite}" });
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, output);
    }

    [Fact]
    public void RealWxcExec_VrPolicyAndLaunch_ConfinesWritesToWorkspace()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows MXC sandbox");
        var wxc = MxcProvisioner.ResolveWxcExec();
        Assert.SkipUnless(wxc is not null, "wxc-exec not provisioned on this host");

        // A workspace at the drive root, NOT nested under %TEMP%/%LOCALAPPDATA% — those
        // are in DefaultWindowsCacheDirs(), and nesting the workspace under a cache root
        // makes their overlapping ACEs break the inner write. Real VR workspaces are
        // repos (e.g. C:\Dev\repo), never under a toolchain-cache dir. Skip if the drive
        // root is not writable (non-admin host).
        var root = Path.Combine(@"C:\", "vr-mxc-it-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var forbidden = Path.Combine(root, "forbidden");
        try { Directory.CreateDirectory(workspace); Directory.CreateDirectory(forbidden); }
        catch (UnauthorizedAccessException) { Assert.Skip("drive root not writable on this host"); return; }
        try
        {
            // VR's REAL generator with the real cache list — the production policy shape.
            var json = MxcPolicyGenerator.Generate(workspace, MxcPolicyGenerator.DefaultWindowsCacheDirs());
            var policyPath = Path.Combine(root, "policy.json");
            File.WriteAllText(policyPath, json); // .NET default: UTF-8, no BOM (what wxc-exec needs)

            // 1) A write OUTSIDE the workspace must be BLOCKED.
            var escape = Path.Combine(forbidden, "escape.txt");
            var (escExit, escOut) = RunMxc(wxc!, policyPath, escape);
            Assert.False(File.Exists(escape),
                $"MXC must block a write outside the workspace (exit={escExit})\n{escOut}\npolicy:\n{json}");

            // 2) A write INSIDE the workspace must SUCCEED.
            var inside = Path.Combine(workspace, "inside.txt");
            var (inExit, inOut) = RunMxc(wxc!, policyPath, inside);
            Assert.True(File.Exists(inside),
                $"MXC must allow a write inside the workspace (exit={inExit})\n{inOut}\npolicy:\n{json}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
