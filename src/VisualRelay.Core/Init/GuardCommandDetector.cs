using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisualRelay.Core.Init;

/// <summary>
/// Detects repo policy guard commands by enumerating <c>tools/guards/*.sh</c>
/// and chaining them with <c> &amp;&amp; </c>. When <c>package.json</c> declares a
/// <c>check</c> or <c>lint</c> script, appends <c>npm run &lt;name&gt;</c>. When a
/// .NET solution file
/// (<c>*.slnx</c> or <c>*.sln</c>) exists in the repo root, appends
/// <c>dotnet format &lt;solution&gt; --verify-no-changes</c>. When a
/// SwiftPM manifest (<c>Package.swift</c>) exists, appends
/// <c>swift build --disable-sandbox</c>. Toolchain checks are appended even when no guard
/// scripts exist. Returns <c>null</c> when neither guards nor a recognized
/// toolchain marker is found — guard detection never blocks init.
/// <para>
/// Every candidate must be corroborated by something the repo itself declares — a
/// script that exists, a manifest that is present. A repo whose CI runs its lint gate
/// as a separate required step used to have that gate dropped entirely, because VR
/// configured only the test command while the stage prompts told the model the harness
/// runs the full check gate at Verify.
/// </para>
/// </summary>
public static partial class GuardCommandDetector
{
    /// <summary>
    /// Detects the guard command or returns <c>null</c> when no guards or
    /// toolchain markers exist.
    /// </summary>
    public static string? Detect(string rootPath)
    {
        var parts = new List<string>();

        // Collect guard scripts when tools/guards/ exists.
        var guardsDir = Path.Combine(rootPath, "tools", "guards");
        if (Directory.Exists(guardsDir))
        {
            var scripts = Directory.EnumerateFiles(guardsDir, "*.sh")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(Path.GetFileName)
                .ToList();

            foreach (var script in scripts)
            {
                parts.Add($"tools/guards/{script}");
            }
        }

        // Append the repo's own policy script when package.json declares one. A check
        // script usually wraps lint, types and formatting, so it wins over a bare lint.
        if (ReadPackageJsonPolicyScript(rootPath) is { } policyScript)
        {
            parts.Add($"npm run {policyScript}");
        }

        // Append dotnet format when a .NET solution file exists.
        var slnx = Directory.EnumerateFiles(rootPath, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var sln = Directory.EnumerateFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var solution = slnx ?? sln;
        if (solution is not null)
        {
            parts.Add($"dotnet format {Path.GetFileName(solution)} --verify-no-changes");
        }

        // Append "swift build" when a SwiftPM manifest exists, with SwiftPM's own manifest
        // sandbox off: it cannot nest inside VR's sandbox on macOS.
        if (File.Exists(Path.Combine(rootPath, "Package.swift")))
        {
            parts.Add("swift build --disable-sandbox");
        }

        if (parts.Count == 0)
            return null;

        return string.Join(" && ", parts);
    }

    /// <summary>
    /// The name of the policy script <c>package.json</c> declares — <c>check</c> first,
    /// then <c>lint</c> — or <c>null</c> when it declares neither (or cannot be read). A script
    /// that rewrites files is passed over: a guard runs after review, and markedjs/marked's
    /// <c>eslint --fix</c> would have edited the task's files there and never failed on a fixable problem.
    /// </summary>
    private static string? ReadPackageJsonPolicyScript(string rootPath)
    {
        try
        {
            var path = Path.Combine(rootPath, "package.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts)
                || scripts.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var name in new[] { "check", "lint" })
            {
                if (scripts.TryGetProperty(name, out var script)
                    && script.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(script.GetString())
                    && !RewritesFiles().IsMatch(script.GetString()!))
                    return name;
            }
        }
        catch
        {
            // Best-effort — an unreadable manifest declares nothing.
        }

        return null;
    }

    // eslint's --fix, prettier's and biome's --write; not --fix-dry-run, which only reports.
    [GeneratedRegex(@"(?<![\w-])--(?:fix|write)(?![\w-])")]
    private static partial Regex RewritesFiles();
}
