using System.Text.Json;
using VisualRelay.Core.Execution.Wsl;
namespace VisualRelay.Core.Execution;
/// <summary>Classifies a single path entry.</summary>
public enum SandboxAccess { ReadOnly, ReadWrite, Blocked }
/// <summary>One resolved path entry with provenance for UI display.</summary>
public sealed record SandboxPathEntry(
    string Raw, string Expanded, SandboxAccess Access, string Source);

/// <summary>
/// The OS nono enforces the profile on, which decides the <c>when</c> predicates
/// in vr-guard and the <c>platform</c> tokens in its groups. A parameter rather
/// than the OS the inspector runs on, because on Windows nono runs inside the WSL
/// distro: the enforced policy there is the Linux one.
/// </summary>
public enum SandboxPlatform { MacOs, Linux }

/// <summary>
/// The complete inspection result. <see cref="Unavailable"/> is returned when
/// nono is absent or a group-expansion call fails.
/// </summary>
public sealed class SandboxInspectionResult
{
    public bool IsAvailable { get; init; }
    public IReadOnlyList<SandboxPathEntry> ReadablePaths { get; init; } = [];
    public IReadOnlyList<SandboxPathEntry> WritablePaths { get; init; } = [];
    public IReadOnlyList<SandboxPathEntry> BlockedPaths { get; init; } = [];

    /// <summary>
    /// One-line reads/writes summary shown above the path lists: reads are the
    /// whole filesystem EXCEPT the blocked paths, which nono enforces on every
    /// platform (inside the WSL distro on Windows). <c>null</c> when unavailable.
    /// </summary>
    public string? ReadsSummary { get; init; }

    public static readonly SandboxInspectionResult Unavailable = new();
}
/// <summary>
/// Resolves the effective sandbox filesystem policy — readable, writable, and
/// blocked paths — by reading the enforced vr-guard profile and expanding
/// inherited groups via the nono binary. Every path entry is derived at
/// runtime; adding a path to vr-guard.json or to an included group shows up
/// here with no code change.
/// </summary>
public static partial class SandboxPathInspector
{
    /// <summary>
    /// Resolves the effective sandbox policy. On macOS and Linux nono is the binary
    /// on PATH (<paramref name="nonoBinary"/> overrides it for tests); on Windows it
    /// is the one inside the resolved WSL distro, asked through wsl.exe
    /// (<see cref="InspectThroughWslAsync"/>), and without a resolved distro the
    /// policy is unavailable. <paramref name="workspaceRoot"/> is the active
    /// workspace granted via <c>--allow-cwd</c> (may be null);
    /// <paramref name="extraAllowPaths"/> are per-repo <c>sandboxExtraAllowPaths</c>.
    /// <para>
    /// Not purely a read on Windows: the distro arm ensures the guard profile is
    /// present inside the distro first (<see cref="NonoProfileEnsurer"/>), because
    /// the profile nono is asked about is a file in the distro user's home and a
    /// fresh distro has none. Off Windows the profile travels in the request.
    /// </para>
    /// </summary>
    public static async Task<SandboxInspectionResult> InspectAsync(
        string? workspaceRoot,
        IReadOnlyList<string>? extraAllowPaths = null,
        string? nonoBinary = null,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            // Awaited, never waited on: the first caller is the desktop app's
            // background inspection, which starts on the UI thread. Blocking there
            // on a probe whose own continuations come back to that same thread is a
            // deadlock, not a delay.
            return await WslContextResolver.TryGetCurrentAsync(cancellationToken) is { } context
                ? await InspectThroughWslAsync(
                    context, RunWslJsonAsync, ct => NonoProfileEnsurer.EnsureAsync(cancellationToken: ct),
                    workspaceRoot, extraAllowPaths, cancellationToken)
                : SandboxInspectionResult.Unavailable;
        }

        var resolvedBinary = nonoBinary ?? PathExecutables.Find("nono");
        if (string.IsNullOrEmpty(resolvedBinary) || !File.Exists(resolvedBinary))
            return SandboxInspectionResult.Unavailable;

        return await ResolveAsync(
            () => RunNonoProfileShowAsync(resolvedBinary, NonoProfileEnsurer.EmbeddedContent, cancellationToken),
            name => RunNonoGroupAsync(resolvedBinary, name, cancellationToken),
            CurrentPlatform, LocalHome, workspaceRoot, extraAllowPaths);
    }

    /// <summary>The platform nono enforces on from this machine: macOS itself, Linux itself, and on Windows the WSL distro.</summary>
    internal static SandboxPlatform CurrentPlatform =>
        OperatingSystem.IsMacOS() ? SandboxPlatform.MacOs : SandboxPlatform.Linux;

    /// <summary>The home <c>~</c> expands against off Windows; null when unknown.</summary>
    private static string? LocalHome
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? null : home;
        }
    }

    /// <summary>
    /// The one pipeline behind both arms. <paramref name="show"/> resolves the
    /// whole extends chain (vr-guard → default) so EVERY inherited group expands —
    /// incl. the deny_* credential/keychain groups — not just vr-guard's own nine;
    /// own directives still come from the embedded profile (a registered copy
    /// `show` reads can be stale), and <paramref name="group"/> supplies each group's
    /// payload. Either query failing degrades to Unavailable.
    /// </summary>
    private static async Task<SandboxInspectionResult> ResolveAsync(
        Func<Task<string?>> show, Func<string, Task<string?>> group,
        SandboxPlatform platform, string? home,
        string? workspaceRoot, IReadOnlyList<string>? extraAllowPaths)
    {
        var showJson = await show();
        if (showJson is null)
            return SandboxInspectionResult.Unavailable;
        var groupEntries = await ExpandInheritedGroupsAsync(showJson, group, platform, home);
        if (groupEntries is null)
            return SandboxInspectionResult.Unavailable;

        var all = new List<SandboxPathEntry>();
        all.AddRange(ParseOwnDirectives(NonoProfileEnsurer.EmbeddedContent, platform, home));
        all.AddRange(groupEntries);
        AddPerRunWritables(all, workspaceRoot, extraAllowPaths, home);
        return BuildResult(all);
    }

    // ── Internal helpers (testable via InternalsVisibleTo) ─────────────────

    /// <summary>
    /// Extracts own allow/read/deny directives from a vr-guard profile JSON, keeping
    /// the <c>when</c> entries of <paramref name="platform"/>. Every entry has
    /// <see cref="SandboxPathEntry.Source"/> = <c>"vr-guard"</c>.
    /// </summary>
    internal static IReadOnlyList<SandboxPathEntry> ParseOwnDirectives(
        string profileJson, SandboxPlatform platform, string? home)
    {
        using var doc = JsonDocument.Parse(profileJson);
        var root = doc.RootElement;
        var entries = new List<SandboxPathEntry>();
        if (!root.TryGetProperty("filesystem", out var fs)) return entries;
        if (fs.TryGetProperty("allow", out var allow))
            entries.AddRange(ParsePathArray(allow, SandboxAccess.ReadWrite, "vr-guard", platform, home));
        if (fs.TryGetProperty("read", out var read))
            entries.AddRange(ParsePathArray(read, SandboxAccess.ReadOnly, "vr-guard", platform, home));
        if (fs.TryGetProperty("deny", out var deny))
            entries.AddRange(ParsePathArray(deny, SandboxAccess.Blocked, "vr-guard", platform, home));
        return entries;
    }

    /// <summary>
    /// Extracts allow.read / allow.readwrite / deny.access from a
    /// <c>nono profile groups &lt;name&gt; --json</c> payload.
    /// Filters by <c>platform</c> for <paramref name="platform"/>; ignores
    /// <c>deny.commands</c> and <c>deny.unlink</c>.
    /// </summary>
    internal static IReadOnlyList<SandboxPathEntry> ParseGroupJson(
        string groupJson, string groupName, SandboxPlatform platform, string? home)
    {
        using var doc = JsonDocument.Parse(groupJson);
        var root = doc.RootElement;
        var entries = new List<SandboxPathEntry>();
        if (root.TryGetProperty("allow", out var allow))
        {
            if (allow.TryGetProperty("read", out var read))
                entries.AddRange(ParseGroupAllowEntries(read, SandboxAccess.ReadOnly, groupName, platform));
            if (allow.TryGetProperty("readwrite", out var rw))
                entries.AddRange(ParseGroupAllowEntries(rw, SandboxAccess.ReadWrite, groupName, platform));
        }
        if (root.TryGetProperty("deny", out var deny) &&
            deny.TryGetProperty("access", out var access))
            entries.AddRange(ParseGroupDenyAccess(access, groupName, platform, home));
        return entries;
    }

    /// <summary>
    /// Resolves <c>$HOME</c> and <c>~</c> prefixes to <paramref name="home"/> (the
    /// distro user's home on Windows). Paths without either prefix, or any path
    /// when the home is unknown, are returned unchanged.
    /// </summary>
    internal static string ExpandPath(string raw, string? home)
    {
        if (string.IsNullOrEmpty(home)) return raw;
        if (raw.StartsWith("$HOME", StringComparison.Ordinal))
            return home + raw["$HOME".Length..];
        if (raw.StartsWith('~'))
            return home + raw[1..];
        return raw;
    }

    private static void AddPerRunWritables(
        List<SandboxPathEntry> all, string? workspaceRoot,
        IReadOnlyList<string>? extraAllowPaths, string? home)
    {
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            // A workspace inside a WSL distro is held as its UNC path; the Linux
            // view is the path nono actually grants, so it is the tooltip.
            var expanded = WslPath.TryParseUnc(workspaceRoot, out _, out var linuxRoot) ? linuxRoot : workspaceRoot;
            all.Add(new SandboxPathEntry(workspaceRoot, expanded, SandboxAccess.ReadWrite, "current workspace"));
        }
        if (extraAllowPaths is { Count: > 0 })
        {
            foreach (var path in extraAllowPaths)
                all.Add(new SandboxPathEntry(path, ExpandPath(path, home),
                    SandboxAccess.ReadWrite, "per-project extras"));
        }
    }

    /// <summary>Reads are the whole filesystem EXCEPT the enforced deny/credential paths.</summary>
    private const string ReadsSummaryText =
        "Reads: the whole filesystem except the blocked paths. " +
        "Writes: only the paths listed here (plus the current workspace).";

    internal static SandboxInspectionResult BuildResult(List<SandboxPathEntry> all) =>
        new()
        {
            IsAvailable = true,
            ReadsSummary = ReadsSummaryText,
            ReadablePaths = all.Where(e => e.Access == SandboxAccess.ReadOnly).ToList(),
            WritablePaths = all.Where(e => e.Access == SandboxAccess.ReadWrite).ToList(),
            BlockedPaths = all.Where(e => e.Access == SandboxAccess.Blocked).ToList(),
        };

    /// <summary>
    /// Parses a mixed array of path strings and <c>{"path":"…","when":"…"}</c>
    /// objects from vr-guard's filesystem sections.
    /// </summary>
    private static IEnumerable<SandboxPathEntry> ParsePathArray(
        JsonElement array, SandboxAccess access, string source, SandboxPlatform platform, string? home)
    {
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var raw = entry.GetString()!;
                yield return new SandboxPathEntry(NormalizeRawForDisplay(raw), ExpandPath(raw, home), access, source);
            }
            else if (entry.ValueKind == JsonValueKind.Object)
            {
                if (!entry.TryGetProperty("path", out var pathProp)) continue;
                var raw = pathProp.GetString()!;
                if (ShouldSkipByWhen(entry, platform)) continue;
                yield return new SandboxPathEntry(NormalizeRawForDisplay(raw), ExpandPath(raw, home), access, source);
            }
        }
    }

    /// <summary>The profile's own <c>when</c> predicate, under the same token rule as the groups' <c>platform</c>.</summary>
    private static bool ShouldSkipByWhen(JsonElement entry, SandboxPlatform platform) =>
        entry.TryGetProperty("when", out var when) && ShouldSkipPlatformToken(when.GetString(), platform);

    /// <summary>
    /// Parses allow.read / allow.readwrite entries from a group JSON:
    /// <c>[{"raw":"…","expanded":"…","platform":"cross-platform"}]</c>.
    /// </summary>
    private static IEnumerable<SandboxPathEntry> ParseGroupAllowEntries(
        JsonElement array, SandboxAccess access, string source, SandboxPlatform platform)
    {
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("raw", out var rawProp)) continue;
            var raw = rawProp.GetString();
            if (string.IsNullOrEmpty(raw)) continue;
            var expanded = entry.TryGetProperty("expanded", out var ep)
                ? (ep.GetString() ?? raw) : raw;
            if (ShouldSkipByPlatform(entry, platform)) continue;
            yield return new SandboxPathEntry(NormalizeRawForDisplay(raw), expanded, access, source);
        }
    }

    private static bool ShouldSkipByPlatform(JsonElement entry, SandboxPlatform platform) =>
        entry.TryGetProperty("platform", out var pp) && ShouldSkipPlatformToken(pp.GetString(), platform);

    /// <summary>Runs <c>nono profile groups &lt;name&gt; --json</c>; stdout or null.</summary>
    private static Task<string?> RunNonoGroupAsync(
        string nonoBinary, string groupName, CancellationToken cancellationToken) =>
        RunNonoJsonAsync(nonoBinary, ["profile", "groups", groupName, "--json"], cancellationToken);
}
