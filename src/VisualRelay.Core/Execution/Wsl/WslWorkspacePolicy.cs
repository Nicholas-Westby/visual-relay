namespace VisualRelay.Core.Execution.Wsl;

public enum WslWorkspaceDecision
{
    Allow,
    Warn,
    Refuse,
}

/// <summary>
/// Where a Windows workspace may live. A repository on a Windows drive is seen
/// inside the distro through DrvFs (<c>/mnt/&lt;letter&gt;/...</c>): roughly an
/// order of magnitude slower for the many small files a build touches, and a
/// permission model Landlock was not designed against. Until the DrvFs
/// confinement probe has been run on a Windows box such workspaces are refused;
/// <see cref="MntPolicy"/> is the one-line downgrade to a warning once it has.
/// </summary>
public static class WslWorkspacePolicy
{
    /// <summary>What a DrvFs workspace gets. Refuse until the DrvFs confinement probe proves enforcement.</summary>
    public const WslWorkspaceDecision MntPolicy = WslWorkspaceDecision.Refuse;

    /// <summary>
    /// Allows a workspace on the distro's own filesystem silently; a DrvFs
    /// workspace gets <see cref="MntPolicy"/> and the message that explains the
    /// cost and where to move the repository.
    /// </summary>
    public static (WslWorkspaceDecision Decision, string? Message) Decide(string linuxWorkspace)
    {
        if (!WslPath.IsMntPath(linuxWorkspace))
            return (WslWorkspaceDecision.Allow, null);

        return (MntPolicy,
            $"The workspace '{linuxWorkspace}' is a Windows drive seen through DrvFs. DrvFs is roughly an order of "
            + "magnitude slower for the many small files a build touches, and its permission model is not the one "
            + "Landlock was designed against; move the repository onto the distro's own filesystem (for example "
            + @"/home/<user>/...) and open it as \\wsl.localhost\<distro>\home\<user>\...");
    }
}
