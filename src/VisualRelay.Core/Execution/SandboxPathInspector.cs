using System.Text.Json;
namespace VisualRelay.Core.Execution;
/// <summary>Classifies a single path entry.</summary>
public enum SandboxAccess { ReadOnly, ReadWrite, Blocked }
/// <summary>One resolved path entry with provenance for UI display.</summary>
public sealed record SandboxPathEntry(
    string Raw, string Expanded, SandboxAccess Access, string Source);
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
    /// One-line reads/writes summary shown above the path lists. Platform-specific
    /// and carried by the result so the UI needs no OS check: the nono (macOS/Linux)
    /// build says reads are the whole filesystem EXCEPT the blocked paths, while the
    /// Windows build says reads are unrestricted (MXC does not read-block the
    /// credential <c>deniedPaths</c>). <c>null</c> when unavailable.
    /// </summary>
    public string? ReadsSummary { get; init; }

    /// <summary>
    /// Windows-only caveat shown against the credential denials (the MXC sandbox
    /// may not enforce <c>deniedPaths</c> yet). <c>null</c> on macOS/Linux, where
    /// nono genuinely enforces the denials and no caveat should appear.
    /// </summary>
    public string? WindowsCredentialCaveat { get; init; }

    /// <summary>Tracking link for the Windows caveat; <c>null</c> when no caveat.</summary>
    public string? WindowsCredentialCaveatUrl { get; init; }

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
    /// Resolves the effective sandbox policy for the current OS.
    /// <paramref name="workspaceRoot"/> is the active workspace granted via
    /// <c>--allow-cwd</c> (may be null). <paramref name="extraAllowPaths"/>
    /// are per-repo <c>sandboxExtraAllowPaths</c>. <paramref name="nonoBinary"/>
    /// overrides the nono path (tests); when null resolves <c>"nono"</c> on PATH.
    /// </summary>
    public static async Task<SandboxInspectionResult> InspectAsync(
        string? workspaceRoot,
        IReadOnlyList<string>? extraAllowPaths = null,
        string? nonoBinary = null,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
            return BuildWindowsResult(workspaceRoot, extraAllowPaths);

        var resolvedBinary = nonoBinary ?? PathExecutables.Find("nono");
        if (string.IsNullOrEmpty(resolvedBinary) || !File.Exists(resolvedBinary))
            return SandboxInspectionResult.Unavailable;

        // Resolve the whole extends chain (vr-guard → swival → default) so EVERY
        // inherited group expands — incl. the deny_* credential/keychain groups —
        // not just vr-guard's own two. Own directives still come from the embedded
        // profile (a registered copy `show` reads can be stale); `show` supplies
        // only the fully-resolved group list, expanded via the existing groups path.
        var profileJson = NonoProfileEnsurer.EmbeddedContent;
        var showJson = await RunNonoProfileShowAsync(resolvedBinary, profileJson, cancellationToken);
        if (showJson is null)
            return SandboxInspectionResult.Unavailable;
        var groupEntries = await ExpandInheritedGroupsAsync(
            showJson, name => RunNonoGroupAsync(resolvedBinary, name, cancellationToken));
        if (groupEntries is null)
            return SandboxInspectionResult.Unavailable;

        var all = new List<SandboxPathEntry>();
        all.AddRange(ParseOwnDirectives(profileJson));
        all.AddRange(groupEntries);
        AddPerRunWritables(all, workspaceRoot, extraAllowPaths);
        return BuildResult(all);
    }

    // ── Internal helpers (testable via InternalsVisibleTo) ─────────────────

    /// <summary>
    /// Extracts own allow/read/deny directives from a vr-guard profile JSON.
    /// Every entry has <see cref="SandboxPathEntry.Source"/> = <c>"vr-guard"</c>.
    /// </summary>
    internal static IReadOnlyList<SandboxPathEntry> ParseOwnDirectives(string profileJson)
    {
        using var doc = JsonDocument.Parse(profileJson);
        var root = doc.RootElement;
        var entries = new List<SandboxPathEntry>();
        if (!root.TryGetProperty("filesystem", out var fs)) return entries;
        if (fs.TryGetProperty("allow", out var allow))
            entries.AddRange(ParsePathArray(allow, SandboxAccess.ReadWrite, "vr-guard"));
        if (fs.TryGetProperty("read", out var read))
            entries.AddRange(ParsePathArray(read, SandboxAccess.ReadOnly, "vr-guard"));
        if (fs.TryGetProperty("deny", out var deny))
            entries.AddRange(ParsePathArray(deny, SandboxAccess.Blocked, "vr-guard"));
        return entries;
    }

    /// <summary>
    /// Extracts allow.read / allow.readwrite / deny.access from a
    /// <c>nono profile groups &lt;name&gt; --json</c> payload.
    /// Filters by <c>platform</c> for the current OS; ignores
    /// <c>deny.commands</c> and <c>deny.unlink</c>.
    /// </summary>
    internal static IReadOnlyList<SandboxPathEntry> ParseGroupJson(
        string groupJson, string groupName)
    {
        using var doc = JsonDocument.Parse(groupJson);
        var root = doc.RootElement;
        var entries = new List<SandboxPathEntry>();
        if (root.TryGetProperty("allow", out var allow))
        {
            if (allow.TryGetProperty("read", out var read))
                entries.AddRange(ParseGroupAllowEntries(read, SandboxAccess.ReadOnly, groupName));
            if (allow.TryGetProperty("readwrite", out var rw))
                entries.AddRange(ParseGroupAllowEntries(rw, SandboxAccess.ReadWrite, groupName));
        }
        if (root.TryGetProperty("deny", out var deny) &&
            deny.TryGetProperty("access", out var access))
            entries.AddRange(ParseGroupDenyAccess(access, groupName));
        return entries;
    }

    /// <summary>
    /// Resolves <c>$HOME</c> and <c>~</c> prefixes to the real home directory.
    /// Paths without either prefix are returned unchanged.
    /// </summary>
    internal static string ExpandPath(string raw)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return raw;
        if (raw.StartsWith("$HOME", StringComparison.Ordinal))
            return home + raw.Substring("$HOME".Length);
        if (raw.StartsWith("~", StringComparison.Ordinal))
            return home + raw.Substring("~".Length);
        return raw;
    }

    // BuildWindowsResult lives in the SandboxPathInspector.Windows.cs partial.

    private static void AddPerRunWritables(
        List<SandboxPathEntry> all, string? workspaceRoot,
        IReadOnlyList<string>? extraAllowPaths)
    {
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
            all.Add(new SandboxPathEntry(workspaceRoot, workspaceRoot,
                SandboxAccess.ReadWrite, "current workspace"));
        if (extraAllowPaths is { Count: > 0 })
        {
            foreach (var path in extraAllowPaths)
                all.Add(new SandboxPathEntry(path, ExpandPath(path),
                    SandboxAccess.ReadWrite, "per-project extras"));
        }
    }

    /// <summary>
    /// The macOS/Linux (nono) reads/writes summary. Reads are the whole filesystem
    /// EXCEPT the enforced deny/credential paths, which nono genuinely blocks.
    /// </summary>
    private const string NonoReadsSummaryText =
        "Reads: the whole filesystem except the blocked paths. " +
        "Writes: only the paths listed here (plus the current workspace).";

    internal static SandboxInspectionResult BuildResult(List<SandboxPathEntry> all) =>
        new()
        {
            IsAvailable = true,
            ReadsSummary = NonoReadsSummaryText,
            ReadablePaths = all.Where(e => e.Access == SandboxAccess.ReadOnly).ToList(),
            WritablePaths = all.Where(e => e.Access == SandboxAccess.ReadWrite).ToList(),
            BlockedPaths = all.Where(e => e.Access == SandboxAccess.Blocked).ToList(),
        };

    /// <summary>
    /// Parses a mixed array of path strings and <c>{"path":"…","when":"…"}</c>
    /// objects from vr-guard's filesystem sections.
    /// </summary>
    private static IEnumerable<SandboxPathEntry> ParsePathArray(
        JsonElement array, SandboxAccess access, string source)
    {
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var raw = entry.GetString()!;
                yield return new SandboxPathEntry(NormalizeRawForDisplay(raw), ExpandPath(raw), access, source);
            }
            else if (entry.ValueKind == JsonValueKind.Object)
            {
                if (!entry.TryGetProperty("path", out var pathProp)) continue;
                var raw = pathProp.GetString()!;
                if (ShouldSkipByWhen(entry)) continue;
                yield return new SandboxPathEntry(NormalizeRawForDisplay(raw), ExpandPath(raw), access, source);
            }
        }
    }

    private static bool ShouldSkipByWhen(JsonElement entry)
    {
        if (!entry.TryGetProperty("when", out var when)) return false;
        var os = when.GetString();
        return (string.Equals(os, "macos", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsMacOS())
            || (string.Equals(os, "linux", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsLinux());
    }

    /// <summary>
    /// Parses allow.read / allow.readwrite entries from a group JSON:
    /// <c>[{"raw":"…","expanded":"…","platform":"cross-platform"}]</c>.
    /// </summary>
    private static IEnumerable<SandboxPathEntry> ParseGroupAllowEntries(
        JsonElement array, SandboxAccess access, string source)
    {
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("raw", out var rawProp)) continue;
            var raw = rawProp.GetString();
            if (string.IsNullOrEmpty(raw)) continue;
            var expanded = entry.TryGetProperty("expanded", out var ep)
                ? (ep.GetString() ?? raw) : raw;
            if (ShouldSkipByPlatform(entry)) continue;
            yield return new SandboxPathEntry(NormalizeRawForDisplay(raw), expanded, access, source);
        }
    }

    private static bool ShouldSkipByPlatform(JsonElement entry) =>
        entry.TryGetProperty("platform", out var pp) && ShouldSkipPlatformToken(pp.GetString());

    /// <summary>Runs <c>nono profile groups &lt;name&gt; --json</c>; stdout or null.</summary>
    private static Task<string?> RunNonoGroupAsync(
        string nonoBinary, string groupName, CancellationToken cancellationToken) =>
        RunNonoJsonAsync(nonoBinary, ["profile", "groups", groupName, "--json"], cancellationToken);
}
