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

    private static string? _cachedWorkspace;
    private static string? _cachedPolicyPath;

    /// <summary>
    /// Writes the VR-authored policy for <paramref name="workspaceRoot"/> to VR's
    /// config dir and returns its absolute path. Memoized by workspace (the cache/
    /// readonly roots are machine-stable), and the write is skipped when the on-disk
    /// bytes already match — so a stage launch does not re-enumerate drives, rebuild
    /// JSON, and rewrite the file every time.
    /// </summary>
    public static string EnsurePolicy(string workspaceRoot)
    {
        if (string.Equals(_cachedWorkspace, workspaceRoot, StringComparison.Ordinal)
            && _cachedPolicyPath is { } cached && File.Exists(cached))
            return cached;

        var configDir = Configuration.XdgConfig.ResolveConfigDir();
        var dir = Path.Combine(configDir, "visual-relay");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, PolicyFileName);
        var json = MxcPolicyGenerator.Generate(
            workspaceRoot,
            MxcPolicyGenerator.DefaultWindowsCacheDirs(),
            MxcPolicyGenerator.DefaultWindowsReadonlyRoots());
        if (!File.Exists(path) || !string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal))
            File.WriteAllText(path, json);

        _cachedWorkspace = workspaceRoot;
        _cachedPolicyPath = path;
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
