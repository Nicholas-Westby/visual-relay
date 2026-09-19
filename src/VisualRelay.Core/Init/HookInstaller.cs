using VisualRelay.Core.Execution;
using VisualRelay.Core.Execution.Wsl;

namespace VisualRelay.Core.Init;

// ReSharper disable once NotAccessedPositionalProperty.Global — 'Path' is part of
// the result contract (the installed hook's path); populated at every construction
// site for callers/diagnostics even though no current consumer reads it.
public sealed record HookInstallResult(bool Installed, string Path, string? Warning);

public static class HookInstaller
{
    private const string Marker = "# Visual Relay pre-commit hook";
    private const string HookFileName = "pre-commit";
    private static readonly TimeSpan ChmodTimeout = TimeSpan.FromSeconds(30);

    private static readonly string HookContent = @"#!/usr/bin/env bash
# Visual Relay pre-commit hook
#
# Enforces commit authority during an active Visual Relay run: when
# .relay/ACTIVE/info.json exists, only the driver's stage-11 commit
# (which carries RELAY_COMMIT_TOKEN in its environment) may commit.
# Commits outside a run are unaffected.
set -euo pipefail

repo_root=""$(git rev-parse --show-toplevel 2>/dev/null)"" || exit 0
active_info=""${repo_root}/.relay/ACTIVE/info.json""

if [[ ! -f ""$active_info"" ]]; then
    exit 0
fi

nonce=""$(grep -o '\""nonce\"" *: *\""[^\""]*\""' ""$active_info"" 2>/dev/null \
    | sed 's/.*\""nonce\"" *: *\""//' | sed 's/\""//' | head -1)""

if [[ -z ""$nonce"" ]]; then
    echo ""Visual Relay: .relay/ACTIVE/info.json is malformed (no nonce)."" >&2
    echo ""A Visual Relay run is active — stage agents must not run git commit."" >&2
    exit 1
fi

if [[ ""${RELAY_COMMIT_TOKEN:-}"" = ""$nonce"" ]]; then
    exit 0
fi

cat >&2 <<'MSG'
Visual Relay: commit rejected — a run is active.
Only the driver's stage-11 sealed commit may land during a run.
Stage agents must not run git commit — the driver produces the single
sealed commit with Task: / Relay-Seal: trailers.
MSG
exit 1
";

    /// <summary>
    /// Installs the Visual Relay pre-commit hook into the target repo.
    /// Idempotent: re-running is safe and reports Installed=true.
    /// Preserves a foreign pre-commit hook (no VR marker) and returns a warning.
    /// Respects an existing git config core.hooksPath. A root inside a WSL distro
    /// gets its hook marked executable from inside the distro
    /// (<see cref="MarkExecutableAsync"/>); <paramref name="runWsl"/> runs that
    /// wsl.exe launch, injected by tests, the real process by default.
    /// </summary>
    public static async Task<HookInstallResult> InstallAsync(
        string rootPath, CancellationToken cancellationToken, IGitInvoker? gitInvoker = null,
        Func<WslLaunch, CancellationToken, Task<(int ExitCode, string Output)>>? runWsl = null)
    {
        var gi = gitInvoker ?? throw new InvalidOperationException("GitInvoker is required but was not provided");
        var run = runWsl ?? RunWslAsync;

        // Guard: without a real repository there is nowhere for a pre-commit hook to
        // run. Refuse rather than fabricate a bogus .git/hooks directory (which would
        // silently never execute). Bootstrap initializes the repo before calling here.
        if (!await GitBootstrapper.IsRepositoryAsync(rootPath, gi, cancellationToken))
        {
            return new HookInstallResult(false, string.Empty,
                $"{rootPath} is not a git repository — run `git init` (or bootstrap the project) " +
                "before installing the Visual Relay pre-commit hook.");
        }

        // Resolve the active hooks directory.
        var hooksDirResult = await gi.RunAsync(
            rootPath, ["config", "--default", ".git/hooks", "core.hooksPath"],
            cancellationToken);
        var hooksDirRelative = hooksDirResult.ExitCode == 0
            ? hooksDirResult.Output.Trim()
            : ".git/hooks";
        var hooksDir = Path.GetFullPath(Path.Combine(rootPath, hooksDirRelative));
        Directory.CreateDirectory(hooksDir);
        var hookPath = Path.Combine(hooksDir, HookFileName);

        // Check for an existing pre-commit hook.
        if (File.Exists(hookPath))
        {
            var existing = await File.ReadAllTextAsync(hookPath, cancellationToken);
            if (existing.Contains(Marker, StringComparison.Ordinal))
            {
                // A hook the repository tracks is the repository's own, marker or not:
                // Visual Relay's own repository ships a superset of this one (its
                // test-budget guard and version bump), and overwriting it dropped both and
                // left a tracked file modified. Its enforcement is already active.
                if (await IsTrackedAsync(gi, rootPath, hookPath, cancellationToken))
                    return new HookInstallResult(true, hookPath, null);

                // VR-owned and untracked — overwrite with the current version.
                return await WriteHookAsync(hookPath, run, cancellationToken);
            }

            // Foreign hook — preserve it.
            var warning = $"A pre-commit hook already exists at {hookPath} and was not written by Visual Relay. " +
                          "It has been left in place. Visual Relay pre-commit enforcement will not be active in this repository.";
            return new HookInstallResult(false, hookPath, warning);
        }

        // No existing hook — install.
        return await WriteHookAsync(hookPath, run, cancellationToken);
    }

    /// <summary>
    /// The wsl.exe launch that marks a hook inside a WSL distro executable
    /// (<c>chmod +x</c> against its Linux path, in the distro the UNC path names),
    /// or null for a native hook path.
    /// </summary>
    internal static WslLaunch? WslChmodLaunch(string hookPath) =>
        WslPath.TryParseUnc(hookPath, out var distro, out var linuxHook)
            ? WslLauncher.BuildPlain(WslExe.Resolve(CurrentContext), distro, ["chmod", "+x", linuxHook])
            : null;

    /// <summary>
    /// Gives the written hook its exec bit. A hook inside a WSL distro was written
    /// through the distro's share, which leaves a default non-executable mode, so
    /// chmod runs inside the distro through <paramref name="runWsl"/>; a native hook
    /// takes the bit directly (Windows has none). Returns the warning that says why
    /// enforcement is inactive when the chmod fails, else null.
    /// </summary>
    internal static async Task<string?> MarkExecutableAsync(
        string hookPath, Func<WslLaunch, CancellationToken, Task<(int ExitCode, string Output)>> runWsl,
        CancellationToken cancellationToken)
    {
        var launch = WslChmodLaunch(hookPath);
        if (launch is null)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(hookPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return null;
        }

        var (exitCode, output) = await runWsl(launch, cancellationToken);
        if (exitCode == 0)
            return null;
        return $"The pre-commit hook was written to {hookPath}, but making it executable inside its WSL distro failed " +
               $"(`{string.Join(' ', launch.Arguments.Skip(3))}` exited {exitCode}: {output.Trim()}). " +
               "Visual Relay pre-commit enforcement will not be active in this repository until the hook is executable.";
    }

    private static WslContext? CurrentContext =>
        OperatingSystem.IsWindows() ? WslContextResolver.TryGetCurrent() : null;

    /// <summary>
    /// Whether git tracks <paramref name="hookPath"/> in the repository at
    /// <paramref name="rootPath"/>. A hook outside the working tree (the default
    /// <c>.git/hooks</c>, or a hooksPath elsewhere) is never tracked.
    /// </summary>
    private static async Task<bool> IsTrackedAsync(
        IGitInvoker gi, string rootPath, string hookPath, CancellationToken cancellationToken)
    {
        var relative = Path.GetRelativePath(rootPath, hookPath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            return false;

        var listed = await gi.RunAsync(rootPath, ["ls-files", "--", relative], cancellationToken);
        return listed.ExitCode == 0 && !string.IsNullOrWhiteSpace(listed.Output);
    }

    private static async Task<HookInstallResult> WriteHookAsync(
        string hookPath, Func<WslLaunch, CancellationToken, Task<(int ExitCode, string Output)>> runWsl,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(hookPath, HookContent, cancellationToken);
        var warning = await MarkExecutableAsync(hookPath, runWsl, cancellationToken);
        return new HookInstallResult(warning is null, hookPath, warning);
    }

    private static async Task<(int ExitCode, string Output)> RunWslAsync(WslLaunch launch, CancellationToken cancellationToken)
    {
        var (exitCode, output, timedOut) = await ProcessCapture.RunAsync(
            launch.FileName, launch.Arguments, Path.GetTempPath(), ChmodTimeout, cancellationToken,
            environment: launch.Environment);
        return (timedOut ? -1 : exitCode, output);
    }
}
