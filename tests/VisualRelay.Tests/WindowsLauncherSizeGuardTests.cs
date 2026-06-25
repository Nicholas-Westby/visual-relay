using VisualRelay.Guards;

namespace VisualRelay.Tests;

/// <summary>
/// Locks the Phase 1 decision that the Windows launchers (<c>visual-relay.cmd</c>
/// and <c>visual-relay.ps1</c>) are <em>explicitly out of scope</em> for the
/// bash-oriented shell-size guard. The guard models POSIX shell specifically —
/// its classifier keys on <c>.sh/.bash/.zsh</c> and <c>#!…sh</c> hashbangs and its
/// line counter understands <c>#</c> comments and <c>&lt;&lt;</c> here-docs — none
/// of which describe PowerShell or batch. The native Windows bootstrap (detect and
/// install the .NET SDK) is irreducibly larger than the Nix-delegating bash
/// launcher and cannot move into C# (it runs before .NET exists). If someone later
/// adds <c>.ps1</c> to the shell extensions, this test fails — forcing the
/// launcher-size question to be confronted, not silently regressed.
/// </summary>
public sealed class WindowsLauncherSizeGuardTests
{
    [Fact]
    public void PowerShellLauncher_IsNotClassifiedAsPosixShellScript()
    {
        Assert.False(ShellScriptClassifier.IsShellScript("visual-relay.ps1", "# bootstrap"));
    }

    [Fact]
    public void CmdShim_IsNotClassifiedAsPosixShellScript()
    {
        Assert.False(ShellScriptClassifier.IsShellScript("visual-relay.cmd", "@echo off"));
    }

    [Fact]
    public void LargePowerShellLauncher_ProducesNoViolation()
    {
        // A 60-line .ps1 is well over the 20-line POSIX limit, yet must not be
        // flagged — it is outside the guard's scope by design.
        var lines = new string[60];
        for (var i = 0; i < lines.Length; i++)
            lines[i] = $"Write-Output {i}";

        var files = new (string Path, string[] Lines)[] { ("visual-relay.ps1", lines) };
        var violations = ShellSizeGuard.FindViolations(files, ShellSizeGuard.DefaultLimit);

        Assert.Empty(violations);
    }
}
