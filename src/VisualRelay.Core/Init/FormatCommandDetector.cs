using System.Text.Json;

namespace VisualRelay.Core.Init;

/// <summary>
/// Detects a whole-project formatter command by inspecting build-system markers
/// in priority order (same order as <see cref="TestCommandDetector"/>).
/// Returns <c>null</c> when no recognized toolchain is found — format detection
/// never blocks init.
/// <para>
/// A toolchain marker alone only proves which toolchain a repo uses, so it settles
/// the formatter only for the ones the toolchain SHIPS (<c>dotnet format</c>,
/// <c>gofmt</c>, <c>cargo fmt</c>) — those need no further evidence and their config
/// files are optional. A THIRD-PARTY formatter is a choice the repo makes, and is
/// configured only when the repo corroborates it with that tool's own config (or its
/// own format script). Without that rule a Swift package that uses Apple's
/// <c>swift-format</c> was handed <c>swiftformat</c> because the binary happened to be
/// on PATH, and the first format step would have rewritten every file in it.
/// </para>
/// </summary>
public static class FormatCommandDetector
{
    /// <summary>
    /// Detects the format command or returns <c>null</c> when no toolchain
    /// markers are found.
    /// </summary>
    public static string? Detect(string rootPath)
    {
        // .NET solution or project
        var slnx = Directory.EnumerateFiles(rootPath, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
        var sln = Directory.EnumerateFiles(rootPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (slnx is not null || sln is not null)
            return $"dotnet format {Path.GetFileName(slnx ?? sln!)}";
        if (TestCommandDetector.HasAnyFile(rootPath, "*.csproj"))
            return "dotnet format";

        // Bun / Node — the repo's own format script wins; prettier only when the repo
        // configures it. A lower-priority marker still gets its turn otherwise.
        if (File.Exists(Path.Combine(rootPath, "package.json")))
        {
            var fmt = ReadPackageJsonFormatScript(rootPath)
                      ?? (HasPrettierConfig(rootPath) ? "prettier --write ." : null);
            if (fmt is not null)
                return fmt;
        }

        // Go
        if (File.Exists(Path.Combine(rootPath, "go.mod")))
            return "gofmt -w .";

        // Rust
        if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
            return "cargo fmt";

        // SwiftPM — swiftformat is third-party and reads its own .swiftformat file.
        // Apple's swift-format is a different tool with a different config, so a repo
        // carrying only .swift-format has not chosen swiftformat.
        if (File.Exists(Path.Combine(rootPath, "Package.swift"))
            && File.Exists(Path.Combine(rootPath, ".swiftformat")))
            return "swiftformat .";

        return null;
    }

    /// <summary>
    /// Whether the repo configures prettier: one of prettier's own config files at the
    /// root (<c>.prettierrc*</c>, <c>prettier.config.*</c>), or a <c>prettier</c> key or
    /// dependency in <c>package.json</c>.
    /// </summary>
    private static bool HasPrettierConfig(string rootPath)
    {
        foreach (var file in Directory.EnumerateFiles(rootPath, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith(".prettierrc", StringComparison.Ordinal)
                || name.StartsWith("prettier.config.", StringComparison.Ordinal))
                return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(rootPath, "package.json")));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("prettier", out _)) return true;
            foreach (var section in new[] { "devDependencies", "dependencies" })
            {
                if (root.TryGetProperty(section, out var deps)
                    && deps.ValueKind == JsonValueKind.Object
                    && deps.TryGetProperty("prettier", out _))
                    return true;
            }
        }
        catch
        {
            // Best-effort — an unreadable package.json corroborates nothing.
        }

        return false;
    }

    private static string? ReadPackageJsonFormatScript(string rootPath)
    {
        try
        {
            var path = Path.Combine(rootPath, "package.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("scripts", out var scripts)
                && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("format", out var formatScript)
                && formatScript.ValueKind == JsonValueKind.String)
            {
                var value = formatScript.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch
        {
            // Best-effort — fall through to prettier on any parse failure.
        }

        return null;
    }
}
