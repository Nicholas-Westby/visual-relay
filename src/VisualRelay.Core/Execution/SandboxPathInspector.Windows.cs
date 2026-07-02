namespace VisualRelay.Core.Execution;

/// <summary>
/// Windows (MXC / <c>wxc-exec</c>) branch of <see cref="SandboxPathInspector"/>.
/// Split into its own partial file to keep the cross-platform base under the
/// 300-line gate and to localize the Windows-only credential-denial caveat: the
/// credential paths VR marks as <c>deniedPaths</c> are surfaced as Blocked, but
/// because native Windows enforcement of denied paths is feature-gated and not
/// shipped in the pinned MXC release, they carry an honest "may be readable"
/// caveat. macOS/Linux (nono) results never carry this caveat.
/// </summary>
public static partial class SandboxPathInspector
{
    /// <summary>
    /// Whether the underlying Windows sandbox (MXC) is known to enforce
    /// <c>filesystem.deniedPaths</c>. Native enforcement is feature-gated
    /// (<c>Feature_BfsPolicyDeny</c>) and NOT shipped in the pinned MXC release
    /// (see PR #489), so this stays <c>false</c> and the credential denials carry
    /// a "may be readable" caveat. Flip to <c>true</c> — a one-line change — to
    /// drop the caveat once enforcement is guaranteed.
    /// </summary>
    // static readonly (not const) so flipping it stays a one-line runtime change
    // and the caveat ternary below is not a compile-time-dead branch.
    internal static readonly bool WindowsDeniedPathsEnforced = false;

    /// <summary>Canonical tracking link for Windows <c>deniedPaths</c> enforcement (MXC PR #489).</summary>
    private const string WindowsDeniedPathsTrackingUrl =
        "https://github.com/microsoft/mxc/pull/489";

    /// <summary>The Windows-only caveat shown against the credential denials.</summary>
    private const string WindowsCredentialCaveatText =
        "⚠ Configured as denied, but the Windows sandbox (MXC) may not enforce " +
        "denied paths yet — treat the credential paths above as potentially readable.";

    /// <summary>
    /// The Windows reads/writes summary. Reads are unrestricted — MXC does not
    /// read-block the credential <c>deniedPaths</c> (see the caveat) — so this must
    /// NOT repeat the nono "except the blocked paths" phrasing.
    /// </summary>
    private const string WindowsReadsSummaryText =
        "Reads: the whole filesystem (not restricted). " +
        "Writes: only the paths listed here (plus the current workspace).";

    /// <summary>
    /// Builds the Windows inspection result: broad reads by MXC default, writes
    /// confined to the cache dirs plus per-run workspace, and the credential
    /// <c>deniedPaths</c> surfaced as Blocked. When
    /// <see cref="WindowsDeniedPathsEnforced"/> is <c>false</c> the result also
    /// carries the caveat + tracking URL so the UI can warn honestly.
    /// <c>internal</c> so tests exercise it OS-independently.
    /// </summary>
    internal static SandboxInspectionResult BuildWindowsResult(
        string? workspaceRoot, IReadOnlyList<string>? extraAllowPaths)
    {
        var writables = new List<SandboxPathEntry>();
        foreach (var dir in MxcPolicyGenerator.DefaultWindowsCacheDirs())
            writables.Add(new SandboxPathEntry(dir, dir, SandboxAccess.ReadWrite, "MXC cache dir"));
        AddPerRunWritables(writables, workspaceRoot, extraAllowPaths);

        var blocked = new List<SandboxPathEntry>
        {
            new("<writes outside listed paths are blocked>",
                "<writes outside listed paths are blocked>",
                SandboxAccess.Blocked, "MXC default"),
        };
        foreach (var dir in MxcPolicyGenerator.WindowsCredentialDenyDirs())
            blocked.Add(new SandboxPathEntry(dir, dir, SandboxAccess.Blocked, "MXC deniedPaths"));

        return new SandboxInspectionResult
        {
            IsAvailable = true,
            ReadsSummary = WindowsReadsSummaryText,
            ReadablePaths = [new SandboxPathEntry(
                "<entire filesystem — reads are not restricted>",
                "<entire filesystem — reads are not restricted>",
                SandboxAccess.ReadOnly, "MXC default")],
            WritablePaths = [.. writables],
            BlockedPaths = [.. blocked],
            WindowsCredentialCaveat = WindowsDeniedPathsEnforced ? null : WindowsCredentialCaveatText,
            WindowsCredentialCaveatUrl = WindowsDeniedPathsEnforced ? null : WindowsDeniedPathsTrackingUrl,
        };
    }
}
