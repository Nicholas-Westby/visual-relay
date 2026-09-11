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
/// policy's answer, as does a UNC pick that points under the distro's /mnt. A UNC
/// pick that is NOT a distro share is refused on Windows: every sandboxed run there
/// goes through the distro, so a foreign share can never become a workspace, and
/// the pick is the only place the operator can be told so.
/// </summary>
public static class FolderPickerWslTranslation
{
    private const string ForeignShareMessage =
        "That folder is a network share Visual Relay cannot use as a workspace. On "
        + "Windows every command runs inside a WSL distro, so only a folder inside one "
        + @"— a path under \\wsl.localhost\<distro> or \\wsl$\<distro> — can be opened.";

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

        return isWindows && IsUnc(picked)
            ? new FolderPick(null, ForeignShareMessage)
            : new FolderPick(picked, null);
    }

    /// <summary>Whether the pick is a UNC path at all (two leading separators).</summary>
    private static bool IsUnc(string picked) =>
        picked.Length > 2 && IsSeparator(picked[0]) && IsSeparator(picked[1]);

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
