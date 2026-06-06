using VisualRelay.Core.Execution;

namespace VisualRelay.Core.Init;

public sealed record HookInstallResult(bool Installed, string Path, string? Warning);

public static class HookInstaller
{
    private const string Marker = "# Visual Relay pre-commit hook";
    private const string HookFileName = "pre-commit";

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
    /// Respects an existing git config core.hooksPath.
    /// </summary>
    public static async Task<HookInstallResult> InstallAsync(string rootPath, CancellationToken cancellationToken)
    {
        // Resolve the active hooks directory.
        var hooksDirResult = await ProcessCapture.RunAsync(
            "git", ["-C", rootPath, "config", "--default", ".git/hooks", "core.hooksPath"],
            rootPath, TimeSpan.FromSeconds(10), cancellationToken);
        var hooksDirRelative = hooksDirResult.ExitCode == 0
            ? hooksDirResult.Output.Trim()
            : ".git/hooks";
        var hooksDir = Path.GetFullPath(Path.Combine(rootPath, hooksDirRelative));
        Directory.CreateDirectory(hooksDir);
        var hookPath = System.IO.Path.Combine(hooksDir, HookFileName);

        // Check for an existing pre-commit hook.
        if (File.Exists(hookPath))
        {
            var existing = await File.ReadAllTextAsync(hookPath, cancellationToken);
            if (existing.Contains(Marker, StringComparison.Ordinal))
            {
                // VR-owned — overwrite with the current version.
                await WriteHookAsync(hookPath, cancellationToken);
                return new HookInstallResult(true, hookPath, null);
            }

            // Foreign hook — preserve it.
            var warning = $"A pre-commit hook already exists at {hookPath} and was not written by Visual Relay. " +
                          "It has been left in place. Visual Relay pre-commit enforcement will not be active in this repository.";
            return new HookInstallResult(false, hookPath, warning);
        }

        // No existing hook — install.
        await WriteHookAsync(hookPath, cancellationToken);
        return new HookInstallResult(true, hookPath, null);
    }

    private static async Task WriteHookAsync(string hookPath, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(hookPath, HookContent, cancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hookPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
