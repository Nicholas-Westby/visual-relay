using System.Text.Json;

namespace VisualRelay.Core.Init;

/// <summary>
/// Detects a whole-project formatter command by inspecting build-system markers
/// in priority order (same order as <see cref="TestCommandDetector"/>).
/// Returns <c>null</c> when no recognized toolchain is found — format detection
/// never blocks init.
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

        // Bun / Node — look for a format script in package.json; fall back to prettier
        if (File.Exists(Path.Combine(rootPath, "package.json")))
        {
            var fmt = ReadPackageJsonFormatScript(rootPath);
            return fmt ?? "prettier --write .";
        }

        // Go
        if (File.Exists(Path.Combine(rootPath, "go.mod")))
            return "gofmt -w .";

        // Rust
        if (File.Exists(Path.Combine(rootPath, "Cargo.toml")))
            return "cargo fmt";

        // SwiftPM — swiftformat is the de-facto formatter.
        if (File.Exists(Path.Combine(rootPath, "Package.swift")))
            return "swiftformat .";

        return null;
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
