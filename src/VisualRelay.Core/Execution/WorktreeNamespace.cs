using System.Security.Cryptography;
using System.Text;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Where a run's throwaway worktrees, verify snapshots and temporary git index
/// files live, named the two ways their two users need it: <see cref="Io"/> is the
/// path .NET opens, <see cref="Git"/> the path the git that serves the workspace is
/// handed. On the local host they are the same string.
/// <para>
/// On Windows the workspace lives inside a WSL distro and its git runs there, to
/// which a Windows temp path (<c>C:\Users\…\Temp\…</c>) is a relative name: a
/// worktree asked for under it is created inside the repository or refused, while
/// .NET goes on writing the run's own files to the Windows path — two halves of one
/// run in two places. So the namespace moves into the distro, under the distro
/// user's cache directory, with the temporary index under <c>/tmp/visual-relay</c>
/// (the same directory the launch envelope creates for its pid files).
/// </para>
/// </summary>
/// <param name="Io">The directory as .NET opens it: the UNC share on the Windows arm.</param>
/// <param name="Git">The directory as the serving git sees it: a Linux path in the distro there.</param>
/// <param name="Distro">The distro that serves it, or null on the local host.</param>
public sealed record WorktreeNamespace(string Io, string Git, string? Distro)
{
    private const string DirName = "visual-relay";
    private const string LinuxTempRoot = "/tmp/" + DirName;

    /// <summary>
    /// The directory the throwaway worktrees of a workspace at
    /// <paramref name="rootPath"/> live under on <paramref name="host"/>.
    /// </summary>
    /// <param name="rootPath">The workspace root, as VR holds it.</param>
    /// <param name="host">Where the sandbox — and the serving git — runs.</param>
    /// <returns>The namespace root in both forms.</returns>
    public static WorktreeNamespace For(string rootPath, SandboxHost host) =>
        ServingDistro(rootPath, host) is { } wsl
            ? InDistro(wsl.Distro, $"{wsl.DistroHome.TrimEnd('/')}/.cache/{DirName}")
            : Local(Path.Combine(Path.GetTempPath(), DirName));

    /// <summary>The directory a temporary git index file for that workspace lives in.</summary>
    /// <param name="rootPath">The workspace root, as VR holds it.</param>
    /// <param name="host">Where the sandbox — and the serving git — runs.</param>
    /// <returns>The directory in both forms.</returns>
    public static WorktreeNamespace TempIndexFor(string rootPath, SandboxHost host) =>
        ServingDistro(rootPath, host) is { } wsl ? InDistro(wsl.Distro, LinuxTempRoot) : Local(Path.GetTempPath());

    /// <summary>
    /// The path git is handed for a directory VR already holds as
    /// <paramref name="ioPath"/> — a worktree it is about to remove, say.
    /// </summary>
    /// <param name="ioPath">The directory as VR holds it.</param>
    /// <param name="host">Where the serving git runs.</param>
    /// <returns>The Linux path inside the distro, or the path unchanged.</returns>
    public static string ForGit(string ioPath, SandboxHost host) =>
        ServingDistro(ioPath, host) is not null && WslPath.TryParseUnc(ioPath, out _, out var linuxPath)
            ? linuxPath
            : ioPath;

    /// <summary>This directory's child, named both ways.</summary>
    /// <param name="segments">The path segments to descend, in order.</param>
    /// <returns>The child namespace.</returns>
    public WorktreeNamespace Child(params string[] segments) =>
        Distro is null
            ? Local(Path.Combine([Io, .. segments]))
            : InDistro(Distro, $"{Git}/{string.Join('/', segments)}");

    /// <summary>
    /// The repo-hash namespace the throwaway worktrees of <paramref name="repoRoot"/>
    /// live in: hashed so concurrent drains of different repositories never collide,
    /// and split by kind so a drain's prune cannot see a live rewrite worktree.
    /// </summary>
    /// <param name="repoRoot">The repository the worktrees belong to.</param>
    /// <param name="isRewrite">Whether these are rewrite worktrees.</param>
    /// <returns>The repo-hash namespace.</returns>
    public WorktreeNamespace Worktrees(string repoRoot, bool isRewrite) =>
        Child(isRewrite ? "wt-rewrite" : "wt", RepoHash(repoRoot));

    private static WorktreeNamespace Local(string path) => new(path, path, null);

    private static WorktreeNamespace InDistro(string distro, string linuxPath) =>
        new(WslPath.ToUnc(distro, linuxPath), linuxPath, distro);

    // The distro whose git serves this workspace, mirroring GitRouting.Decide: a UNC
    // root names its own distro and must be the resolved one; an absolute Linux root
    // belongs to the resolved distro. A Windows drive root is served by Git for
    // Windows (and refused by the workspace policy), so its namespace stays put.
    private static WslContext? ServingDistro(string rootPath, SandboxHost host) =>
        host.Wsl is { } wsl
        && (WslPath.TryParseUnc(rootPath, out var distro, out _)
            ? distro.Equals(wsl.Distro, StringComparison.OrdinalIgnoreCase)
            : rootPath.StartsWith('/'))
            ? wsl
            : null;

    private static string RepoHash(string repoRoot) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repoRoot))))[..12].ToLowerInvariant();
}
