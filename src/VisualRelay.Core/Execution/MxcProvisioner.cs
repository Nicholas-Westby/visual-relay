namespace VisualRelay.Core.Execution;

/// <summary>
/// Resolves the Windows sandbox runtime: locates <c>wxc-exec</c> (the MXC CLI,
/// from PATH or the per-user provisioned cache) and writes the VR-authored MXC
/// policy beside VR's other config. Mirrors how <see cref="NonoProfileEnsurer"/>
/// owns the nono profile on Unix. The full execution plan — mode plus the resolved
/// wxc-exec and policy paths — is produced by <see cref="ResolvePlan"/>.
/// </summary>
public static class MxcProvisioner
{
    private const string PolicyFileName = "mxc-policy.json";

    /// <summary>The pinned wxc-exec cache: <c>%LOCALAPPDATA%\visual-relay\mxc\wxc-exec.exe</c>.</summary>
    public static string CachedWxcExecPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "visual-relay", "mxc", "wxc-exec.exe");

    /// <summary>Resolves wxc-exec from PATH (PATHEXT-aware) or the provisioned cache; null when absent.</summary>
    public static string? ResolveWxcExec()
    {
        var onPath = PathExecutables.Find("wxc-exec");
        if (onPath is not null)
            return onPath;
        var cached = CachedWxcExecPath();
        return File.Exists(cached) ? cached : null;
    }

    /// <summary>
    /// Writes the VR-authored policy for <paramref name="workspaceRoot"/> to VR's
    /// config dir and returns its absolute path. Regenerated each call (the cache
    /// dirs and workspace can change), like the nono profile's overwrite-always.
    /// </summary>
    public static string EnsurePolicy(string workspaceRoot)
    {
        var configDir = Configuration.XdgConfig.ResolveConfigDir();
        var dir = Path.Combine(configDir, "visual-relay");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, PolicyFileName);
        var json = MxcPolicyGenerator.Generate(
            workspaceRoot,
            MxcPolicyGenerator.DefaultWindowsCacheDirs(),
            MxcPolicyGenerator.DefaultWindowsReadonlyRoots());
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>
    /// The full Windows execution plan: the selected mode plus the wxc-exec and
    /// policy paths (non-null only in MXC mode). Reads the opt-in env and the real
    /// wxc-exec availability; writes the policy only when MXC is selected.
    /// </summary>
    public static (WindowsSandboxMode Mode, string? WxcExec, string? PolicyPath) ResolvePlan(string? workspaceRoot)
    {
        var optIn = Environment.GetEnvironmentVariable(WindowsSandbox.OptInEnvVar);
        var wxc = ResolveWxcExec();
        var mode = WindowsSandbox.Select(optIn, wxc is not null);
        var policy = mode == WindowsSandboxMode.Mxc && workspaceRoot is not null
            ? EnsurePolicy(workspaceRoot)
            : null;
        return (mode, wxc, policy);
    }
}
