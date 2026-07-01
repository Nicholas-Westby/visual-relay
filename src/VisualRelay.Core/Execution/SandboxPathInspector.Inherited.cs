using System.Diagnostics;
using System.Text.Json;

namespace VisualRelay.Core.Execution;

/// <summary>
/// Inherited-profile resolution for <see cref="SandboxPathInspector"/>. The nono
/// branch must expand the WHOLE <c>extends</c> chain (vr-guard → swival → default),
/// not just vr-guard's own two groups. <c>nono profile show &lt;profile&gt; --json</c>
/// resolves that chain; its <c>groups.include</c> is then expanded group-by-group
/// through the same <c>nono profile groups &lt;name&gt; --json</c> path the own groups
/// already use — so the inherited <c>deny_*</c> groups (credentials, keychains, shell
/// history, …) finally surface in the Blocked list, and inherited allow/read groups
/// fill Readable/Writable. Kept in a partial file to respect the per-file line budget.
/// </summary>
public static partial class SandboxPathInspector
{
    /// <summary>
    /// Extracts the fully-resolved <c>groups.include</c> from a
    /// <c>nono profile show &lt;profile&gt; --json</c> payload — the effective chain
    /// (vr-guard → swival → default) with every inherited group present.
    /// </summary>
    internal static IReadOnlyList<string> ParseResolvedGroupIncludes(string showJson)
    {
        using var doc = JsonDocument.Parse(showJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("groups", out var groups)) return [];
        if (!groups.TryGetProperty("include", out var include)) return [];
        IEnumerable<string> names = include.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!);
        // Honour groups.exclude so an excluded group is never displayed as enforced.
        if (groups.TryGetProperty("exclude", out var exclude))
        {
            var excluded = exclude.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            names = names.Where(n => !excluded.Contains(n));
        }
        return names.ToList();
    }

    /// <summary>
    /// Expands every group named in a resolved <paramref name="showJson"/> include list
    /// by fetching its <c>nono profile groups &lt;name&gt; --json</c> payload through
    /// <paramref name="groupJsonProvider"/> and classifying it (<c>deny_*</c> → Blocked,
    /// allow.read → Readable, allow.readwrite → Writable), honouring each group's
    /// <c>platform</c> filter. Returns <c>null</c> if ANY group fails to resolve —
    /// preserving the graceful-degradation contract (caller maps null → Unavailable).
    /// The provider is the only nono seam, so tests feed payloads without shelling out.
    /// </summary>
    internal static async Task<IReadOnlyList<SandboxPathEntry>?> ExpandInheritedGroupsAsync(
        string showJson, Func<string, Task<string?>> groupJsonProvider)
    {
        try
        {
            var entries = new List<SandboxPathEntry>();
            foreach (var name in ParseResolvedGroupIncludes(showJson))
            {
                var groupJson = await groupJsonProvider(name);
                if (groupJson is null) return null;
                entries.AddRange(ExpandGroup(groupJson, name));
            }
            return entries;
        }
        // A nono that exits 0 but prints non-JSON / an unexpected shape must degrade
        // to Unavailable, never throw (VR resolves nono by bare name — foreign/older
        // binaries are possible). Real cancellation still propagates.
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Classifies one group payload, first applying the group-level <c>platform</c>
    /// filter (<c>*_macos</c> / <c>*_linux</c> groups contribute only on their OS;
    /// cross-platform or unmarked groups always contribute), then delegating per-entry
    /// classification to <see cref="ParseGroupJson"/>.
    /// </summary>
    internal static IReadOnlyList<SandboxPathEntry> ExpandGroup(string groupJson, string groupName)
        => ShouldSkipGroupByPlatform(groupJson) ? [] : ParseGroupJson(groupJson, groupName);

    /// <summary>
    /// True when the group's top-level <c>platform</c> names the OTHER OS. Delegates to
    /// <see cref="ShouldSkipPlatformToken"/> so group-level and per-entry filtering stay
    /// identical; "cross-platform", absent, or an unrecognised token never skips.
    /// </summary>
    private static bool ShouldSkipGroupByPlatform(string groupJson)
    {
        using var doc = JsonDocument.Parse(groupJson);
        return doc.RootElement.TryGetProperty("platform", out var pp)
            && ShouldSkipPlatformToken(pp.GetString());
    }

    /// <summary>
    /// The single platform-token rule shared by the group-level and per-entry filters:
    /// skip ONLY when the token names the OTHER OS ("macos" off macOS, "linux" off
    /// Linux). "cross-platform", absent/null, or any unrecognised value is included.
    /// </summary>
    internal static bool ShouldSkipPlatformToken(string? platform) =>
        (string.Equals(platform, "macos", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsMacOS())
        || (string.Equals(platform, "linux", StringComparison.OrdinalIgnoreCase) && !OperatingSystem.IsLinux());

    /// <summary>
    /// Parses a group's <c>deny.access</c> array, accepting BOTH bare strings and the
    /// real nono object form <c>{"raw":…,"expanded":…}</c>; each becomes a Blocked entry
    /// with its displayed <c>Raw</c> normalized to the <c>~</c> convention.
    /// Any per-entry <c>platform</c> is honoured too.
    /// </summary>
    private static IEnumerable<SandboxPathEntry> ParseGroupDenyAccess(
        JsonElement access, string groupName)
    {
        foreach (var entry in access.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var s = entry.GetString()!;
                yield return new SandboxPathEntry(NormalizeRawForDisplay(s), ExpandPath(s), SandboxAccess.Blocked, groupName);
            }
            else if (entry.ValueKind == JsonValueKind.Object &&
                     entry.TryGetProperty("raw", out var rawProp))
            {
                if (ShouldSkipByPlatform(entry)) continue;
                var raw = rawProp.GetString();
                if (string.IsNullOrEmpty(raw)) continue;
                var expanded = entry.TryGetProperty("expanded", out var ep)
                    ? (ep.GetString() ?? raw) : raw;
                yield return new SandboxPathEntry(NormalizeRawForDisplay(raw), expanded, SandboxAccess.Blocked, groupName);
            }
        }
    }

    /// <summary>
    /// Resolves the effective <c>extends</c> chain by running
    /// <c>nono profile show &lt;profile&gt; --json</c> against the embedded vr-guard
    /// profile. The embedded content is written to a throwaway temp file (nono resolves
    /// swival → default from its own registry), so the resulting group list reflects the
    /// EXACT enforced profile with no registered-copy staleness. Returns stdout on exit
    /// 0, else <c>null</c>; never throws (missing nono / IO / non-zero exit → null).
    /// </summary>
    private static async Task<string?> RunNonoProfileShowAsync(
        string nonoBinary, string profileJson, CancellationToken cancellationToken)
    {
        string? tempPath = null;
        try
        {
            tempPath = Path.Combine(Path.GetTempPath(),
                "vr-guard-resolve-" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(tempPath, profileJson, cancellationToken);
            return await RunNonoJsonAsync(
                nonoBinary, ["profile", "show", tempPath, "--json"], cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
        finally
        {
            if (tempPath is not null)
                try { File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// Shared launcher for read-only <c>nono … --json</c> queries used by both the
    /// group-expansion and profile-show paths. Runs with a 10s timeout; returns stdout
    /// on exit 0, else <c>null</c>. Cancellation from the caller propagates; the timeout
    /// kills the process tree and degrades to <c>null</c> (never throws for nono issues).
    /// </summary>
    private static async Task<string?> RunNonoJsonAsync(
        string nonoBinary, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = nonoBinary,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null) return null;
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token, cancellationToken);
            string stdout;
            try
            {
                stdout = await process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            await stderrTask;
            await process.WaitForExitAsync(CancellationToken.None);
            return process.ExitCode == 0 ? stdout : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return null; }
    }
}
