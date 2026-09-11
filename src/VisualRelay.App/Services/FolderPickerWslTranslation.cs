using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.App.Services;

/// <summary>
/// The outcome of a folder pick: the root to open, or null with the message that
/// explains the refusal. An accepted root may carry a message too (a warning).
/// </summary>
public sealed record FolderPick(string? Root, string? Message);

/// <summary>
/// What Browse does with the folder the operator picked. A folder inside a WSL
/// distro (<c>\\wsl$</c> or <c>\\wsl.localhost</c>) is kept as the root in its
/// UNC form: .NET IO reads it and <see cref="GitRouting"/> runs git inside the
/// distro for it, so nothing is translated here; the Linux path is derived where
/// nono or git need it (<see cref="WslPath"/>). A Windows drive folder picked on
/// Windows would be a DrvFs workspace inside the distro, so it gets the workspace
/// policy's answer, as does a UNC pick that points under the distro's /mnt.
/// </summary>
public static class FolderPickerWslTranslation
{
    public static FolderPick Decide(string picked, bool isWindows)
    {
        if (WslPath.TryParseUnc(picked, out _, out var linuxPath)
            || (isWindows && WslPath.TryDriveToMnt(picked, out linuxPath)))
        {
            var (decision, message) = WslWorkspacePolicy.Decide(linuxPath);
            return decision == WslWorkspaceDecision.Refuse
                ? new FolderPick(null, message)
                : new FolderPick(picked, message);
        }

        return new FolderPick(picked, null);
    }
}
