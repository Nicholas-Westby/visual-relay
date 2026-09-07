namespace VisualRelay.Core.Init;

/// <summary>
/// Detects repo policy guard commands by enumerating <c>tools/guards/*.sh</c>
/// and chaining them with <c> &amp;&amp; </c>. When a .NET solution file
/// (<c>*.slnx</c> or <c>*.sln</c>) exists in the repo root, appends
/// <c>dotnet format &lt;solution&gt; --verify-no-changes</c>. When a
/// SwiftPM manifest (<c>Package.swift</c>) exists, appends
/// <c>swift build</c>. Toolchain checks are appended even when no guard
/// scripts exist. Returns <c>null</c> when neither guards nor a recognized
/// toolchain marker is found — guard detection never blocks init.
/// </summary>
public static class GuardCommandDetector
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

        // Append dotnet format when a .NET solution file exists.
        var slnx = Directory.EnumerateFiles(rootPath, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var sln = Directory.EnumerateFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var solution = slnx ?? sln;
        if (solution is not null)
        {
            parts.Add($"dotnet format {Path.GetFileName(solution)} --verify-no-changes");
        }

        // Append "swift build" when a SwiftPM manifest exists.
        if (File.Exists(Path.Combine(rootPath, "Package.swift")))
        {
            parts.Add("swift build");
        }

        if (parts.Count == 0)
            return null;

        return string.Join(" && ", parts);
    }
}
